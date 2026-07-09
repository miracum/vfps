window.vfpsCsvUpload = {
    uploadFile: async function (inputElementId, presignedUrl) {
        const input = document.getElementById(inputElementId);
        if (!input || !input.files || input.files.length === 0) {
            throw new Error("No file selected.");
        }

        const file = input.files[0];

        // Bytes go straight from the browser to S3 via this presigned URL - never through the
        // Blazor circuit or Kestrel, so multi-GB files aren't bounded by SignalR message size.
        const response = await fetch(presignedUrl, {
            method: "PUT",
            body: file,
            headers: { "Content-Type": "text/csv" },
        });

        if (!response.ok) {
            throw new Error(`Upload failed with status ${response.status}`);
        }

        input.value = "";
    },
};
