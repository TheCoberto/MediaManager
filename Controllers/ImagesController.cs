using MediaManager.Models;
using Dapper;
using System;
using System.Configuration;
using System.Data;
using System.Data.SqlClient;
using System.IO;
using System.Linq;
using System.Web;
using System.Web.Mvc;
using MediaManager.Filters;

namespace MediaManager.Controllers
{
    [AuthFilter]
    [Feature("Images", enabledByDefault: true)]
    public class ImagesController : Controller
    {
        private static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".gif", ".webp" };

        // The guest home page: every uploaded image, newest first.
        [GuestAllowed]
        public ActionResult ImageViewAll()
        {
            return View(DapperORM.ReturnList<ImageModel>("ImageSelectAll"));
        }

        public ActionResult ImageManager()
        {
            return View(DapperORM.ReturnList<ImageModel>("ImageSelectAll"));
        }

        [HttpGet]
        public ActionResult ImageViewById(int id = 0)
        {
            if (id == 0)
                return View();
            else
            {
                DynamicParameters param = new DynamicParameters();
                param.Add("@ImageId", id);
                return View(DapperORM.ReturnList<ImageModel>("ImageSelectById", param).FirstOrDefault<ImageModel>());
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult ImageDelete(int id)
        {
            DynamicParameters param = new DynamicParameters();
            param.Add("@ImageId", id);
            var image = DapperORM.ReturnList<ImageModel>("ImageSelectById", param).FirstOrDefault();

            // Move the file to the Recycle Bin first (with its caption, so Restore puts it back on the wall).
            if (image != null)
            {
                string path = MediaPaths.ResolveFile(SiteSettings.ImageUploadFolder, image.Title);
                if (path != null && System.IO.File.Exists(path))
                    RecycleBin.Recycle(path, RecycleBin.SourceImages, image.Description);
            }

            DapperORM.ExecuteWithoutReturn("ImageDeleteById", param);
            return RedirectToAction("ImageManager");
        }

        [HttpPost]
        [GuestAllowed]
        [ValidateAntiForgeryToken]
        public ActionResult ImageAdd(HttpPostedFileBase file, string description)
        {
            // The upload page sends files one at a time with JavaScript and wants a JSON answer per file;
            // a plain form post (no JavaScript) still gets the old redirects.
            bool ajax = Request.IsAjaxRequest();
            ActionResult Fail(string message) => ajax
                ? (ActionResult)Json(new { success = false, message })
                : RedirectToAction("UploadFailed");

            if (file == null || file.ContentLength <= 0)
                return Fail("No file was received.");
            if (file.ContentLength > UploadLimit)
                return Fail("This image is too big. The limit is " + ChunkedUpload.FormatLimit(UploadLimit) + ".");

            string error = CheckImage(file.FileName, description, out string fileExt, out string folder);
            if (error != null)
                return Fail(error);

            string fileName = PlaceImage(file.FileName, fileExt, folder, description, file.SaveAs);

            if (ajax)
                return Json(new { success = true, fileName, message = "Uploaded." });

            // Guests go back to the wall to see their upload; the owner goes to the manager page as before.
            return Roles.IsGuest(Session)
                ? RedirectToAction("ImageViewAll")
                : RedirectToAction("ImageManager");
        }

        /// <summary>Guest Uploads, one piece at a time (what the upload page uses). Same checks as ImageAdd.</summary>
        [HttpPost]
        [GuestAllowed]
        [ValidateAntiForgeryToken]
        public JsonResult ImageAddChunk(string uploadId, long offset, long totalSize, string fileName, string description, HttpPostedFileBase chunk)
        {
            string error = CheckImage(fileName, description, out string fileExt, out string folder);
            if (error != null)
            {
                ChunkedUpload.Discard(uploadId);
                return Json(new { success = false, message = error });
            }

            var r = ChunkedUpload.Append(uploadId, offset, totalSize, UploadLimit, chunk);
            if (!r.Ok)
                return Json(new { success = false, message = r.Error, receivedSoFar = r.ReceivedSoFar });
            if (!r.Complete)
                return Json(new { success = true, complete = false });

            try
            {
                string saved = PlaceImage(fileName, fileExt, folder, description, target => System.IO.File.Move(r.PartPath, target));
                return Json(new { success = true, complete = true, fileName = saved, message = "Uploaded." });
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                ChunkedUpload.Discard(uploadId);
                return Json(new { success = false, message = "Couldn't save the image: " + ex.Message });
            }
        }

        /// <summary>Guests get the smaller GuestMaxUploadMB limit; the owner uploading here gets MaxUploadMB.</summary>
        private long UploadLimit => Roles.IsGuest(Session) ? SiteSettings.GuestMaxUploadBytes : SiteSettings.MaxUploadBytes;

        /// <summary>Checks a guest upload's caption and type. Returns an error message, or null if it's fine.</summary>
        private static string CheckImage(string originalName, string description, out string fileExt, out string folder)
        {
            fileExt = Path.GetExtension(originalName ?? "").ToLowerInvariant();
            folder = null;

            if (string.IsNullOrWhiteSpace(description))
                return "Please enter a name or caption.";
            if (!ImageExtensions.Contains(fileExt))
                return "Only .jpg, .jpeg, .png, .gif and .webp images can be uploaded.";

            folder = MediaPaths.ResolveFolder(SiteSettings.ImageUploadFolder);
            return folder == null ? "The upload folder isn't set up correctly." : null;
        }

        /// <summary>Saves the image under a clean, unused name and adds it to the Guest Uploads list. Returns the saved name.</summary>
        private static string PlaceImage(string originalName, string fileExt, string folder, string description, Action<string> place)
        {
            // Clean up the name (phones send things like "IMG_0001.JPG"); drop characters that break URLs.
            string baseName = new string(Path.GetFileNameWithoutExtension(Path.GetFileName(originalName ?? ""))
                .Where(c => !Path.GetInvalidFileNameChars().Contains(c) && c != '#' && c != '%' && c != '+')
                .ToArray()).Trim();
            if (baseName.Length == 0) baseName = "image";
            if (baseName.Length > 60) baseName = baseName.Substring(0, 60);

            Directory.CreateDirectory(folder);

            // Never overwrite an earlier upload with the same name: "photo.jpg" -> "photo (2).jpg"
            string fileName = baseName + fileExt;
            for (int n = 2; System.IO.File.Exists(Path.Combine(folder, fileName)); n++)
                fileName = $"{baseName} ({n}){fileExt}";

            string filePath = Path.Combine(folder, fileName);
            place(filePath);

            using (var sqlconn = new SqlConnection(ConfigurationManager.ConnectionStrings["DefaultConnection"].ConnectionString))
            using (var sqlcomm = new SqlCommand("ImageInsert", sqlconn) { CommandType = CommandType.StoredProcedure })
            {
                sqlconn.Open();
                sqlcomm.Parameters.AddWithValue("@Title", fileName);
                sqlcomm.Parameters.AddWithValue("@Description", description.Trim());
                sqlcomm.Parameters.AddWithValue("@FileExt", fileExt);
                sqlcomm.Parameters.AddWithValue("@FilePath", filePath);
                sqlcomm.ExecuteNonQuery();
            }

            return fileName;
        }

        [GuestAllowed]
        public ActionResult ImageAdd()
        {
            return View();
        }

        [GuestAllowed]
        public ActionResult ImageViewAllShuffled()
        {
            return View(DapperORM.ReturnList<ImageModel>("ImageSelectAll"));
        }

        [GuestAllowed]
        public ActionResult UploadFailed()
        {
            return View();
        }

        [HttpGet]
        public ActionResult ImageEdit(int id = 0)
        {
            if (id == 0)
                return View();
            else
            {
                DynamicParameters param = new DynamicParameters();
                param.Add("@ImageId", id);
                return View(DapperORM.ReturnList<ImageModel>("ImageSelectById", param).FirstOrDefault<ImageModel>());
            }
        }

        [HttpPost]
        public ActionResult ImageEdit(ImageModel imageModel)
        {

            DynamicParameters param = new DynamicParameters();
            param.Add("@ImageId", imageModel.ImageId);
            param.Add("@Description", imageModel.Description);
            DapperORM.ExecuteWithoutReturn("ImageUpdate", param);

            return RedirectToAction("ImageManager");
        }   
    }
}
