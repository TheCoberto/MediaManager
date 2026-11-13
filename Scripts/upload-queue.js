/*
    upload-queue.js: shared by the Add Media and Guest Uploads pages.

    Files go up one after another, each IN PIECES (10 MB by default), so:
      - there's no size limit except the site's MaxUploadMB / GuestMaxUploadMB settings
        (no 500 MB / 2 GB request ceiling, no request timeouts)
      - every file gets its own progress and its own success/error, and a bad file doesn't stop the rest
      - a dropped connection only re-sends the piece it was on (3 tries)
    The server joins the pieces (Models/ChunkedUpload.cs) and moves the finished file into place.
*/
(function () {
    "use strict";

    var DEFAULT_CHUNK = 10 * 1024 * 1024;
    var TRIES = 3;

    function formatSize(bytes) {
        if (bytes >= 1073741824) return (bytes / 1073741824).toFixed(2) + " GB";
        if (bytes >= 1048576) return (bytes / 1048576).toFixed(1) + " MB";
        if (bytes >= 1024) return Math.round(bytes / 1024) + " KB";
        return bytes + " bytes";
    }

    function isImage(file) { return /^image\//.test(file.type) || /\.(jpe?g|png|gif|webp|heic)$/i.test(file.name); }
    function isVideo(file) { return /^video\//.test(file.type) || /\.(mp4|mov|avi|webm)$/i.test(file.name); }
    function isAudio(file) { return /^audio\//.test(file.type) || /\.(mp3|wav|m4a|aac|ogg|oga|opus|flac)$/i.test(file.name); }

    // 32 hex characters. (crypto.randomUUID only exists on https pages; getRandomValues works on http too.)
    function newUploadId() {
        var bytes = new Uint8Array(16);
        (window.crypto || window.msCrypto).getRandomValues(bytes);
        return Array.prototype.map.call(bytes, function (b) { return ("0" + b.toString(16)).slice(-2); }).join("");
    }

    function sleep(ms) { return new Promise(function (r) { setTimeout(r, ms); }); }

    /**
     * POSTs one piece. Resolves with { ok, message, data, retry } where retry = true means
     * "worth trying again" (network error, server error) rather than a real "no".
     */
    function postPiece(url, token, fields, blob, fileName, onLoaded) {
        return new Promise(function (resolve) {
            var form = new FormData();
            form.append("__RequestVerificationToken", token);
            Object.keys(fields).forEach(function (k) { form.append(k, fields[k]); });
            form.append("chunk", blob, fileName);

            var xhr = new XMLHttpRequest();
            xhr.open("POST", url, true);
            xhr.setRequestHeader("X-Requested-With", "XMLHttpRequest");

            xhr.upload.addEventListener("progress", function (e) {
                if (e.lengthComputable && onLoaded) onLoaded(e.loaded);
            });

            xhr.addEventListener("load", function () {
                var data = null;
                try { data = JSON.parse(xhr.responseText); } catch (_) { }

                if (xhr.status >= 200 && xhr.status < 300 && data && typeof data === "object")
                    return resolve({ ok: data.success !== false, message: data.message || "", data: data });

                if (xhr.status === 401) // log in in another tab so this list (and what's uploaded) stays put
                    return resolve({ ok: false, message: "Your login has expired. Log in again in another tab, then press Retry here." });
                if (xhr.status === 403)
                    return resolve({ ok: false, message: "You don't have permission to upload here." });
                if (xhr.status >= 200 && xhr.status < 300) // a page instead of an answer: the login page
                    return resolve({ ok: false, message: "Your login may have expired. Refresh the page and log in again." });
                resolve({ ok: false, retry: true, message: "The server had a problem (" + xhr.status + ")." });
            });

            xhr.addEventListener("error", function () {
                resolve({ ok: false, retry: true, message: "Network error: check the connection and try again." });
            });

            xhr.send(form);
        });
    }

    /**
     * Uploads one file in pieces. Always resolves (never rejects) with { ok, message, data }:
     * data is the server's answer to the last piece (e.g. data.fileName = the name it was saved as).
     * fields are sent with every piece. onProgress(percent) is called as it goes.
     */
    async function sendChunked(url, token, fields, file, onProgress, chunkSize) {
        chunkSize = chunkSize || DEFAULT_CHUNK;
        if (!file.size) return { ok: false, message: "The file is empty." };

        var id = newUploadId();
        var offset = 0;
        var result = null;

        while (offset < file.size) {
            var end = Math.min(offset + chunkSize, file.size);
            var blob = file.slice(offset, end);
            var start = offset;
            var meta = Object.assign({}, fields, { uploadId: id, offset: start, totalSize: file.size, fileName: file.name });

            for (var attempt = 1; ; attempt++) {
                result = await postPiece(url, token, meta, blob, file.name, function (loaded) {
                    if (onProgress) onProgress(Math.min(99, Math.floor((start + loaded) / file.size * 100)));
                });
                if (result.ok) break;

                // The piece got there but the answer didn't (then we re-sent it): carry on from where the server is.
                if (result.data && result.data.receivedSoFar === end) { result = { ok: true, data: { complete: false } }; break; }

                if (!result.retry || attempt >= TRIES) return result;
                await sleep(1500 * attempt);
            }
            offset = end;
        }

        if (onProgress) onProgress(100);
        return result;
    }

    window.UploadQueue = {
        formatSize: formatSize,
        isImage: isImage,
        isVideo: isVideo,
        isAudio: isAudio,
        sendChunked: sendChunked
    };
})();
