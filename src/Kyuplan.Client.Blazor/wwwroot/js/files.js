// Saves a file that the page holds in memory (a backup) through the browser's download.
window.ppFiles = {
    save: function (fileName, bytes) {
        // bytes arrives as a Uint8Array (Blazor passes a byte[] as base64 and decodes it for us).
        const url = URL.createObjectURL(new Blob([bytes], { type: 'application/zip' }));
        const link = document.createElement('a');
        link.href = url;
        link.download = fileName;
        document.body.appendChild(link);
        link.click();
        link.remove();
        setTimeout(function () { URL.revokeObjectURL(url); }, 10000);
    }
};
