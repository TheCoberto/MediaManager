/*
    media-common.js: shared by the Gallery, View, Favorites, Storage Random and Flagged pages (with Views/Shared/_MediaTools.cshtml).

    MediaTools.mediaHtml / renderPlayers show any kind of file (image, video, audio, PDF, text, other
    documents); pages rendered on the server leave <div class="media-player" data-src data-file ...> placeholders.

    MediaTools.rename / move / remove / markCorrupted / unmark / fix / setFavorite each show their dialog or
    confirmation, call the server, and return a Promise that resolves on success (and never resolves
    if you cancel). Also sets up the video helpers every page wants: keep-your-place after full screen,
    pause other videos when one plays, pause videos scrolled out of view, and J/K/L keyboard shortcuts.
*/
(function ($) {
    "use strict";

    var URLS = window.MEDIA_URLS || {};

    // ---------- helpers ----------

    function escapeHtml(s) {
        return String(s).replace(/[&<>"']/g, function (c) {
            return { "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c];
        });
    }

    function normalizeFolder(s) {
        return (s || "").trim().replace(/\\/g, "/").replace(/^\/+|\/+$/g, "");
    }

    function showToast(message) {
        var toast = $("#toast");
        toast.stop(true, true).text(message).fadeIn(200);
        clearTimeout(showToast._t);
        showToast._t = setTimeout(function () { toast.fadeOut(400); }, 2000);
    }

    function token() {
        return $('input[name="__RequestVerificationToken"]').first().val();
    }

    // Login expired (the server answers 401 to background requests): a bar at the top with a way back in.
    // Logging in brings you back to this page.
    function showLoginExpired() {
        if ($("#loginExpiredBar").length) return;
        var login = "/Login?returnUrl=" + encodeURIComponent(location.pathname + location.search);
        $('<div id="loginExpiredBar" role="alert"></div>')
            .css({ position: "fixed", top: 0, left: 0, right: 0, zIndex: 10000, padding: "12px", textAlign: "center",
                   background: "#b71c1c", color: "#fff", fontWeight: "bold", boxShadow: "0 2px 10px rgba(0,0,0,.4)" })
            .append(document.createTextNode("Your login has expired, so that didn't go through. "))
            .append($('<a></a>').attr("href", login).text("Log in again").css({ color: "#fff", textDecoration: "underline" }))
            .appendTo("body");
    }

    // Any background request (gallery loading, folder checks, ...) that finds the login expired
    $(document).ajaxError(function (e, xhr) {
        if (xhr.status === 401) showLoginExpired();
    });

    /**
     * POST with the security token. Resolves with the JSON response when it says success; otherwise shows
     * the error and rejects with an Error whose .response is the server's answer.
     * handled(res): optional; return true to deal with a "no" answer yourself (no alert).
     */
    function post(url, data, handled) {
        return new Promise(function (resolve, reject) {
            function fail(msg, res, silent) {
                if (!silent) alert("Error: " + msg);
                var err = new Error(msg);
                err.response = res;
                reject(err);
            }

            $.post(url, $.extend({ __RequestVerificationToken: token() }, data))
                .done(function (res) {
                    if (res && typeof res === "object" && res.success !== false) return resolve(res);
                    if (!res || typeof res !== "object") { showLoginExpired(); return fail("Your login has expired.", null, true); }
                    fail(res.message || "Something went wrong.", res, !!(handled && handled(res)));
                })
                .fail(function (xhr) {
                    if (xhr.status === 401) return fail("Your login has expired.", xhr.responseJSON, true); // the bar is showing
                    fail(xhr.status === 403 ? "You don't have permission to do that." : "Request failed (" + xhr.status + ").", xhr.responseJSON);
                });
        });
    }

    // ---------- actions ----------

    function rename(folder, file) {
        return new Promise(function (resolve) {
            $("#renameInput").val(file);
            $("#renameModal").show();
            $("#renameInput").focus();

            $("#renameConfirmBtn").off("click.mt").on("click.mt", function () {
                var newName = $("#renameInput").val().trim();
                if (!newName) return alert("Enter a new name");
                if (newName === file) { $("#renameModal").hide(); return; }

                post(URLS.rename, { folder: folder, oldName: file, newName: newName }).then(function () {
                    $("#renameModal").hide();
                    showToast("File renamed");
                    resolve(newName);
                }, function () { });
            });
            $("#renameInput").off("keydown.mt").on("keydown.mt", function (e) {
                if (e.key === "Enter") { e.preventDefault(); $("#renameConfirmBtn").click(); }
            });
        });
    }

    // Move dialog: searchable folder picker; you can still type/edit a path, including a new folder.
    var allFolders = null; // loaded once per page

    function loadFolders() {
        if (allFolders) return $.Deferred().resolve(allFolders).promise();
        return $.getJSON(URLS.folders, { v: Date.now() }).then(function (list) { return (allFolders = list || []); });
    }

    function renderFolderList(currentFolder) {
        var typed = normalizeFolder($("#moveInput").val());
        var q = typed.toLowerCase();
        var list = (allFolders || []).filter(function (f) {
            return f !== currentFolder && (!q || f.toLowerCase().indexOf(q) >= 0);
        });

        $("#moveFolderList").html(
            list.slice(0, 300).map(function (f) {
                return '<div class="move-folder-option" data-folder="' + escapeHtml(f) + '">' + escapeHtml(f) + "</div>";
            }).join("") || '<div style="padding:6px 8px; opacity:.7;">No matching folders</div>'
        );

        var exists = (allFolders || []).some(function (f) { return f.toLowerCase() === q; });
        $("#moveHint").text(
            !q ? list.length + " folders. Type to search, or type a new folder path."
                : exists ? "Existing folder."
                    : list.length ? 'Pick a folder above, or keep "' + typed + '" to create it as a new folder.'
                        : '"' + typed + "\" doesn't exist yet. It will be created."
        );
    }

    function move(folder, file) {
        return new Promise(function (resolve) {
            $("#moveFileName").text(file);
            $("#moveInput").val("");
            $("#moveFolderList").html('<div style="padding:6px 8px; opacity:.7;">Loading folders...</div>');
            $("#moveHint").text("");
            $("#moveModal").show();
            $("#moveInput").focus();

            var render = function () { renderFolderList(folder); };
            loadFolders().then(render, function () { $("#moveHint").text("Couldn't load folders. You can still type a path."); });

            $("#moveInput").off("input.mt").on("input.mt", render);
            $("#moveInput").off("keydown.mt").on("keydown.mt", function (e) {
                if (e.key === "Enter") { e.preventDefault(); $("#moveConfirmBtn").click(); }
            });
            // mousedown (not click) so picking a folder doesn't blur the input first
            $(document).off("mousedown.mt").on("mousedown.mt", ".move-folder-option", function (e) {
                e.preventDefault();
                $("#moveInput").val($(this).attr("data-folder")).focus();
                render();
            });

            $("#moveConfirmBtn").off("click.mt").on("click.mt", function () {
                var newFolder = normalizeFolder($("#moveInput").val());
                if (!newFolder) return alert("Pick or type a folder");

                function send(keepBoth) {
                    post(URLS.move, { folder: folder, file: file, newFolder: newFolder, keepBoth: keepBoth },
                        function (res) { return !!res.conflict; } // a name clash is handled below, not with an alert
                    ).then(function (res) {
                        $("#moveModal").hide();
                        showToast(res.newName && res.newName !== file
                            ? "Moved to " + newFolder + " as " + res.newName
                            : "File moved to " + newFolder);
                        // A newly created folder shows up in the picker next time
                        if (allFolders && !allFolders.some(function (f) { return f.toLowerCase() === newFolder.toLowerCase(); }))
                            allFolders.push(newFolder);
                        resolve(newFolder);
                    }, function (err) {
                        var res = err && err.response;
                        if (!res || !res.conflict) return;
                        // Never overwrite: offer to keep both, the moved one under a free name
                        if (confirm(res.message + "\n\nMove it anyway and keep both? This one will be named \"" + res.suggestedName + "\"."))
                            send(true);
                    });
                }
                send(false);
            });
        });
    }

    function remove(folder, file) {
        return new Promise(function (resolve) {
            if (!confirm('Delete "' + file + '"?\n\nIt will be moved to the Recycle Bin, where you can restore it.')) return;
            post(URLS.remove, { folder: folder, file: file }).then(function () {
                showToast("Moved to the Recycle Bin");
                resolve();
            }, function () { });
        });
    }

    function markCorrupted(folder, file) {
        return new Promise(function (resolve) {
            if (!confirm('Mark "' + file + '" as corrupted?')) return;
            post(URLS.markCorrupted, { folder: folder, fileName: file }).then(function () {
                showToast("File marked as corrupted");
                resolve();
            }, function () { });
        });
    }

    // Clears a Corrupted / Marked-for-deletion flag (no confirmation: it's easy to flag again).
    // nameOnly: an old flag saved by file name only, whose file isn't anywhere now (Flagged page).
    function unmark(folder, file, nameOnly) {
        return post(URLS.unmark, { folder: folder, fileName: file, nameOnly: !!nameOnly }).then(function () {
            showToast("Flag cleared");
        });
    }

    // Resolves true when fixed, false if you cancelled; rejects if it failed.
    // onStart runs once you confirm (e.g. to show "Fixing..." on the button).
    function fix(folder, file, onStart) {
        if (!confirm('Run "' + file + '" through FFmpeg to fix it? This may take a minute.')) return Promise.resolve(false);
        if (onStart) onStart();
        showToast("Fixing file, please wait...");
        return post(URLS.fix, { fileName: file, folder: folder }).then(function () { showToast("File fixed!"); return true; });
    }

    function setFavorite(folder, file, favorite) {
        return post(URLS.setFavorite, { folder: folder, file: file, favorite: favorite }).then(function () {
            showToast(favorite ? "Added to Favorites" : "Removed from Favorites");
            return favorite;
        });
    }

    // ---------- showing any kind of file ----------

    var KINDS = window.MEDIA_KINDS || {}; // { image: [".jpg", ...], video: [...], audio: [...], document: [...] }

    /** "image" | "video" | "audio" | "document" | null */
    function kindOf(file) {
        var m = /\.[^.\/\\]+$/.exec(file || "");
        var ext = m ? m[0].toLowerCase() : "";
        for (var k in KINDS) if (KINDS.hasOwnProperty(k) && KINDS[k].indexOf(ext) >= 0) return k;
        return null;
    }

    function isPdf(file) { return /\.pdf$/i.test(file || ""); }
    function isText(file) { return /\.(txt|md)$/i.test(file || ""); }
    function extLabel(file) { var m = /\.([^.]+)$/.exec(file || ""); return m ? m[1].toUpperCase() : "File"; }

    function iconFor(file) {
        switch (kindOf(file)) {
            case "audio": return "🎵";
            case "video": return "🎬";
            case "document": return isPdf(file) ? "📕" : isText(file) ? "📄" : "📝";
            default: return "🖼️";
        }
    }

    /**
     * HTML that shows / plays a file of any kind.
     * opts.id         id for the main element (e.g. "mediaElement")
     * opts.autoplay   start video / audio playing
     * opts.compact    list view (Gallery, Favorites, Flagged): small text preview, PDFs as a card
     * opts.maxHeight  CSS max height (default 80vh)
     * opts.sizeAttrs  extra attributes for images / videos, e.g. 'width="500" height="400"'
     * opts.thumb      thumbnail URL (lists): shown instead of a big picture, and as a video's poster so
     *                 nothing of the video downloads until you press play
     */
    function mediaHtml(url, file, opts) {
        opts = opts || {};
        var id = opts.id ? ' id="' + escapeHtml(opts.id) + '"' : "";
        var src = escapeHtml(url);
        var maxH = opts.maxHeight || "80vh";
        var auto = opts.autoplay ? " autoplay" : "";
        var size = opts.sizeAttrs ? " " + opts.sizeAttrs : "";
        var thumb = opts.thumb ? escapeHtml(opts.thumb) : "";

        switch (kindOf(file)) {
            case "video":
                return '<video' + id + size + ' controls loop' + auto
                    + (thumb ? ' preload="none" poster="' + thumb + '"' : ' preload="metadata"')
                    + ' style="max-width:100%; max-height:' + maxH + ';"><source src="' + src + '"></video>';
            case "audio":
                return '<div class="audio-card"><div class="kind-icon">🎵</div>'
                    + '<audio' + id + ' controls preload="metadata"' + auto + ' src="' + src + '"></audio></div>';
            case "document":
                if (isPdf(file) && !opts.compact) {
                    return '<iframe' + id + ' class="doc-frame" src="' + src + '" title="' + escapeHtml(file) + '" style="height:' + maxH + ';"></iframe>'
                        + '<div class="doc-note">Not showing? (some phones can\'t show PDFs inside a page) '
                        + '<a href="' + src + '" target="_blank" rel="noopener">Open the PDF in a new tab</a></div>';
                }
                if (isText(file)) {
                    return '<pre' + id + ' class="doc-text" data-src="' + src + '" data-max="' + (opts.compact ? 4096 : 1048576) + '"'
                        + ' style="max-height:' + (opts.compact ? "300px" : maxH) + ';">Loading...</pre>';
                }
                return '<div' + id + ' class="doc-card"><div class="kind-icon">' + iconFor(file) + '</div>'
                    + '<div>' + escapeHtml(extLabel(file)) + ' document'
                    + (isPdf(file) ? "" : '<br><small>Browsers can\'t show this type. Download it to open it.</small>') + '</div></div>';
            default:
                return '<img' + id + size + ' src="' + (thumb || src) + '" loading="lazy" alt="" style="max-width:100%; max-height:' + maxH + ';">';
        }
    }

    // Fill in text previews (<pre class="doc-text" data-src=...>): only the first part of big files is fetched.
    function loadTextPreviews(root) {
        $(root || document).find("pre.doc-text[data-src]").each(function () {
            var pre = this;
            var url = pre.getAttribute("data-src");
            var max = parseInt(pre.getAttribute("data-max"), 10) || 1048576;
            pre.removeAttribute("data-src");

            fetch(url, { credentials: "same-origin", headers: { Range: "bytes=0-" + (max - 1) } })
                .then(function (r) {
                    if (!r.ok) throw new Error(r.status);
                    var range = r.headers.get("Content-Range");            // "bytes 0-4095/123456" when cut short
                    var total = range ? parseInt(range.split("/")[1], 10) : null;
                    return r.text().then(function (text) {
                        var cut = (total && total > max) || text.length > max;
                        if (text.length > max) text = text.slice(0, max);
                        pre.textContent = text.length ? text : "(empty file)";
                        if (cut) {
                            var more = document.createElement("div");
                            more.className = "doc-note";
                            more.textContent = "Showing the start of the file only. Download it to see all of it.";
                            pre.parentNode.insertBefore(more, pre.nextSibling);
                        }
                    });
                })
                .catch(function () { pre.textContent = "Couldn't load this file."; });
        });
    }

    // Server-rendered pages leave <div class="media-player" data-src data-file [data-id data-autoplay data-compact data-max-height data-thumb]>
    function renderPlayers(root) {
        $(root || document).find(".media-player[data-src]").each(function () {
            var el = this;
            el.innerHTML = mediaHtml(el.getAttribute("data-src"), el.getAttribute("data-file"), {
                thumb: el.getAttribute("data-thumb"),
                id: el.getAttribute("data-id"),
                autoplay: el.getAttribute("data-autoplay") === "true",
                compact: el.getAttribute("data-compact") === "true",
                maxHeight: el.getAttribute("data-max-height"),
                sizeAttrs: el.getAttribute("data-size-attrs")
            });
            el.removeAttribute("data-src");
        });
        loadTextPreviews(root);
    }

    function download(url, file) {
        var a = document.createElement("a");
        a.href = url;
        a.download = file || "";
        document.body.appendChild(a);
        a.click();
        a.remove();
    }

    function viewUrl(folder, file) {
        return URLS.view + "?folder=" + encodeURIComponent(folder) + "&file=" + encodeURIComponent(file);
    }

    function galleryUrl(folder) {
        return URLS.gallery + "?folder=" + encodeURIComponent(folder);
    }

    // ---------- video helpers ----------

    function pauseVideosOutOfView() {
        document.querySelectorAll("video").forEach(function (video) {
            var rect = video.getBoundingClientRect();
            var fullyVisible = rect.top >= 0 && rect.bottom <= (window.innerHeight || document.documentElement.clientHeight);
            if (!fullyVisible && !video.paused) video.pause();
        });
    }

    // Keep your place when you leave a full-screen video.
    //
    // Why this is tricky on phones: by the time the browser says "now full screen", it has often
    // already laid the page out again and reset the scroll to the top. Phones also fire resize
    // (and pause) events while ENTERING full screen. The old version saved the position too late
    // and treated those events as "exited", so it scrolled you to the top.
    //
    // So: remember the video and where it sat on screen the moment you touch it (before the phone
    // moves anything), only trust real full-screen enter/exit signals, and on exit scroll that same
    // video back to the same spot, re-checking a few times while the layout settles.
    function setupFullscreenFix() {
        var anchor = null;      // { video, offset }: the video you touched and its distance from the top of the screen
        var fsVideo = null;     // the video that is full screen right now
        var userScrolled = false;

        function fullscreenElement() { return document.fullscreenElement || document.webkitFullscreenElement || null; }
        function inFullscreen() { return !!fsVideo || !!fullscreenElement(); }

        function remember(video) {
            if (!video || inFullscreen()) return;
            anchor = { video: video, offset: video.getBoundingClientRect().top };
        }

        // The touch/click on the video (or its full-screen button) happens before full screen starts
        ["pointerdown", "touchstart", "mousedown"].forEach(function (evt) {
            document.addEventListener(evt, function (e) {
                var v = e.target && e.target.closest ? e.target.closest("video") : null;
                if (v) remember(v);
            }, { capture: true, passive: true });
        });

        // Starting playback is also a good moment to remember where you are
        document.addEventListener("play", function (e) {
            if (e.target.tagName === "VIDEO") remember(e.target);
        }, true);

        function entered(video) {
            if (!video || video.tagName !== "VIDEO") return;
            if (!anchor || anchor.video !== video) anchor = { video: video, offset: 80 }; // never touched: put it near the top
            fsVideo = video;
        }

        function exited() {
            var video = fsVideo, a = anchor;
            fsVideo = null;
            if (!video || !a || a.video !== video || !document.body.contains(video)) return;

            userScrolled = false;
            var restore = function () {
                if (userScrolled || inFullscreen()) return;
                var target = video.getBoundingClientRect().top + window.scrollY - a.offset;
                if (Math.abs(window.scrollY - target) > 2) window.scrollTo(0, Math.max(0, target));
            };
            // Phones keep adjusting the layout for a moment after leaving full screen
            requestAnimationFrame(restore);
            [150, 400, 800].forEach(function (ms) { setTimeout(restore, ms); });
        }

        // If you start scrolling yourself, stop correcting
        ["touchmove", "wheel"].forEach(function (evt) {
            window.addEventListener(evt, function () { if (!inFullscreen()) userScrolled = true; }, { passive: true });
        });

        // Android Chrome, desktop browsers
        ["fullscreenchange", "webkitfullscreenchange"].forEach(function (evt) {
            document.addEventListener(evt, function () {
                var el = fullscreenElement();
                if (el) entered(el.tagName === "VIDEO" ? el : (el.querySelector && el.querySelector("video")));
                else if (fsVideo) exited();
            });
        });

        // iPhone/iPad Safari: its own video player fires these on the <video> instead
        document.addEventListener("webkitbeginfullscreen", function (e) { entered(e.target); }, true);
        document.addEventListener("webkitendfullscreen", function () { if (fsVideo) exited(); }, true);
    }

    function setupVideoHelpers() {
        // Only one video / audio plays at a time
        document.addEventListener("play", function (e) {
            if (e.target.tagName !== "VIDEO" && e.target.tagName !== "AUDIO") return;
            document.querySelectorAll("video, audio").forEach(function (v) { if (v !== e.target) v.pause(); });
        }, true);

        window.addEventListener("scroll", pauseVideosOutOfView);
        window.addEventListener("resize", pauseVideosOutOfView);

        // J / K / L: back 10s, play/pause, forward 10s (hovered video/audio, else the first one)
        document.addEventListener("keydown", function (e) {
            if (["INPUT", "TEXTAREA", "SELECT"].indexOf(e.target.tagName) >= 0) return;
            var video = document.querySelector("video:hover, audio:hover") || document.querySelector("video, audio");
            if (!video) return;

            switch (e.key.toLowerCase()) {
                case "j": video.currentTime = Math.max(0, video.currentTime - 10); break;
                case "k": video.paused ? video.play() : video.pause(); break;
                case "l": video.currentTime = Math.min(video.duration, video.currentTime + 10); break;
            }
        });

        setupFullscreenFix();
    }

    setupVideoHelpers();

    // This script loads at the end of the page, so placeholders are already there.
    renderPlayers(document);

    window.MediaTools = {
        kindOf: kindOf,
        mediaHtml: mediaHtml,
        renderPlayers: renderPlayers,
        loadTextPreviews: loadTextPreviews,
        download: download,
        escapeHtml: escapeHtml,
        normalizeFolder: normalizeFolder,
        showToast: showToast,
        showLoginExpired: showLoginExpired,
        post: post,
        rename: rename,
        move: move,
        remove: remove,
        markCorrupted: markCorrupted,
        unmark: unmark,
        fix: fix,
        setFavorite: setFavorite,
        viewUrl: viewUrl,
        galleryUrl: galleryUrl,
        pauseVideosOutOfView: pauseVideosOutOfView
    };
})(jQuery);
