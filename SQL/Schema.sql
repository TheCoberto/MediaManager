/*
    Schema.sql: everything the MediaManager app needs in a site database
    (one database per site, e.g. MediaManager, or PrivateMedia for a second site).

    Safe to run any number of times against a new OR existing database:
      - tables are only created if missing (existing data is never touched or dropped)
      - stored procedures are created or updated to the versions below

    New database from scratch: easiest is the script below, which also creates the database.
    In SSMS instead: run  CREATE DATABASE MediaManager  (or your site's database name) first, select it, then run this file.

    Run against the target database, e.g. in SSMS (pick the database first), or:
      .\deploy\Setup-Database.ps1 -Database MediaManager
*/

SET NOCOUNT ON;
GO

-- ============================================================
-- Tables
-- ============================================================

-- Text columns are NVARCHAR (file names and captions can be in any language, or have emoji) with real
-- maximum lengths: file names up to 400 characters, folders up to 1000, like the rest of the tables.

-- Corrupted / to-be-deleted flags from the gallery and Storage Random
IF OBJECT_ID('dbo.LogTable', 'U') IS NULL
CREATE TABLE dbo.LogTable (
    LogID       INT IDENTITY(1,1) PRIMARY KEY,
    FileName    NVARCHAR(400) NOT NULL,
    ToBeDeleted BIT NOT NULL DEFAULT 0,
    Corrupted   BIT NOT NULL DEFAULT 0,
    CreatedAt   DATETIME DEFAULT GETDATE()
);
GO

-- Guest Uploads page (ImagesController)
IF OBJECT_ID('dbo.Images', 'U') IS NULL
CREATE TABLE dbo.Images (
    ImageId     INT IDENTITY(1,1) PRIMARY KEY,
    Title       NVARCHAR(400)  NOT NULL,     -- the saved file name
    Artist      NVARCHAR(200)  NULL,         -- not used any more
    Description NVARCHAR(1000) NULL,         -- caption (the upload form allows 200)
    FileSize    INT            NULL,
    FilePath    NVARCHAR(1000) NULL,
    FileExt     VARCHAR(10)    NOT NULL      -- ".jpg"
);
GO

-- ------------------------------------------------------------
-- Upgrade older databases: these two tables used VARCHAR (English letters only: any other character,
-- like é in some cases, Chinese, or an emoji, was saved as "?" so the file could never be found again)
-- and VARCHAR(MAX). Converts them in place; only runs where a column still has the old type.
-- ------------------------------------------------------------
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.LogTable') AND name = 'FileName' AND (system_type_id <> 231 OR max_length <> 800))
    ALTER TABLE dbo.LogTable ALTER COLUMN FileName NVARCHAR(400) NOT NULL;
GO
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Images') AND name = 'Title' AND (system_type_id <> 231 OR max_length <> 800))
    ALTER TABLE dbo.Images ALTER COLUMN Title NVARCHAR(400) NOT NULL;
GO
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Images') AND name = 'Artist' AND (system_type_id <> 231 OR max_length <> 400))
    ALTER TABLE dbo.Images ALTER COLUMN Artist NVARCHAR(200) NULL;
GO
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Images') AND name = 'Description' AND (system_type_id <> 231 OR max_length <> 2000))
    ALTER TABLE dbo.Images ALTER COLUMN Description NVARCHAR(1000) NULL;
GO
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Images') AND name = 'FilePath' AND max_length = -1)
    ALTER TABLE dbo.Images ALTER COLUMN FilePath NVARCHAR(1000) NULL;
GO
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Images') AND name = 'FileExt' AND max_length = -1)
    ALTER TABLE dbo.Images ALTER COLUMN FileExt VARCHAR(10) NOT NULL;
GO

-- Flags belong to one file (folder + name). Older rows were saved by name only, so a flag showed on every
-- file with that name in any folder; those keep Folder = NULL and still apply to the name until a file gets
-- its own newer row. Folder is relative to the media root ('' = the top folder).
IF COL_LENGTH('dbo.LogTable', 'Folder') IS NULL
    ALTER TABLE dbo.LogTable ADD Folder NVARCHAR(1000) NULL;
GO

-- Flags are looked up by file name, newest first (gallery pages, Flagged page). Rebuilt once to include Folder.
IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.LogTable') AND name = 'IX_LogTable_FileName_CreatedAt')
   AND NOT EXISTS (SELECT 1 FROM sys.index_columns ic
                   JOIN sys.indexes i ON i.object_id = ic.object_id AND i.index_id = ic.index_id
                   WHERE i.object_id = OBJECT_ID('dbo.LogTable') AND i.name = 'IX_LogTable_FileName_CreatedAt'
                     AND COL_NAME(ic.object_id, ic.column_id) = 'Folder')
    DROP INDEX IX_LogTable_FileName_CreatedAt ON dbo.LogTable;
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.LogTable') AND name = 'IX_LogTable_FileName_CreatedAt')
    CREATE INDEX IX_LogTable_FileName_CreatedAt ON dbo.LogTable (FileName, CreatedAt DESC) INCLUDE (Folder, Corrupted, ToBeDeleted);
GO

-- Top-level folders hidden from the home page folder list ("Hide folders" dropdown)
IF OBJECT_ID('dbo.HiddenFolders', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.HiddenFolders (FolderName NVARCHAR(400) NOT NULL PRIMARY KEY);
    INSERT INTO dbo.HiddenFolders (FolderName) VALUES (N'Unorganized Files');  -- hidden by default
END
GO

-- Site Recycle Bin: files deleted from the UI are moved to <media root>\.recycle\<id>\<file name>
IF OBJECT_ID('dbo.RecycleBin', 'U') IS NULL
CREATE TABLE dbo.RecycleBin (
    RecycleId        INT IDENTITY(1,1) PRIMARY KEY,
    OriginalFolder   NVARCHAR(1000) NOT NULL,          -- relative to the media root ('' = root)
    FileName         NVARCHAR(400)  NOT NULL,
    StoredPath       NVARCHAR(1000) NOT NULL,          -- relative to the media root, e.g. .recycle/<id>/<file>
    FileSize         BIGINT         NOT NULL,
    DeletedAt        DATETIME       NOT NULL DEFAULT GETDATE(),
    Source           NVARCHAR(50)   NULL,              -- 'Gallery' or 'Images' (Guest Uploads)
    ImageDescription NVARCHAR(MAX)  NULL               -- caption, so restoring a guest upload puts it back on the Guest Uploads page
);
GO
-- Whole folders can be recycled too (one row per folder: FileName = folder name, FileSize = total bytes)
IF COL_LENGTH('dbo.RecycleBin', 'IsFolder') IS NULL
    ALTER TABLE dbo.RecycleBin ADD IsFolder BIT NOT NULL CONSTRAINT DF_RecycleBin_IsFolder DEFAULT 0;
GO
IF COL_LENGTH('dbo.RecycleBin', 'FileCount') IS NULL
    ALTER TABLE dbo.RecycleBin ADD FileCount INT NULL;   -- files inside a recycled folder
GO
-- (The old Videos and Sounds pages were removed: dbo.Videos / dbo.Sounds and their procedures are no
-- longer created. Existing ones are left alone; drop them by hand if you want them gone.)

-- Owner's favorite files (Favorites page, star buttons). Kept in step with Rename/Move/Delete done through the site.
IF OBJECT_ID('dbo.Favorites', 'U') IS NULL
CREATE TABLE dbo.Favorites (
    FavoriteId INT IDENTITY(1,1) PRIMARY KEY,
    Folder     NVARCHAR(1000) NOT NULL,     -- relative to the media root ('' = root)
    FileName   NVARCHAR(400)  NOT NULL,
    CreatedAt  DATETIME       NOT NULL DEFAULT GETDATE()
);
GO
-- ============================================================
-- Stored procedures (used by the Guest Uploads page)
-- ============================================================
GO
CREATE OR ALTER PROCEDURE dbo.ImageSelectAll AS
BEGIN
	SELECT ImageId, Title, Artist, [Description], FileSize, FilePath
	FROM Images
END
GO

CREATE OR ALTER PROCEDURE dbo.ImageSelectById (
	@ImageId int
) AS
BEGIN
	SELECT ImageId, Title, Artist, [Description], FileSize, FilePath
	FROM Images
	WHERE ImageId = @ImageId
END
GO

CREATE OR ALTER PROCEDURE dbo.ImageInsert (
	@Title nvarchar(400),
	@Description nvarchar(1000),
	@FilePath nvarchar(1000),
	@FileExt varchar(10)
) AS
BEGIN
	INSERT INTO Images (Title, Description, FilePath, FileExt)
	VALUES (@Title, @Description, @FilePath, @FileExt)
END
GO

CREATE OR ALTER PROCEDURE dbo.ImageUpdate (
	@ImageId int,
	@Description nvarchar(1000)
) AS
BEGIN
	UPDATE Images 
	SET [Description] = @Description
	WHERE @ImageId = ImageId
END
GO

CREATE OR ALTER PROCEDURE dbo.ImageDeleteById (
	@ImageId int
) AS
BEGIN
	DELETE 
	FROM Images
	WHERE ImageId = @ImageId
END
GO

PRINT 'Schema is up to date.';
GO
