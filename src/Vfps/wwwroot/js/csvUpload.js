function parseCsvLine(line, delimiter) {
    const fields = [];
    let current = "";
    let inQuotes = false;

    for (let i = 0; i < line.length; i++) {
        const ch = line[i];

        if (!inQuotes && ch === '"' && current === "") {
            inQuotes = true;
            continue;
        }

        if (inQuotes && ch === '"') {
            if (line[i + 1] === '"') {
                current += '"';
                i++;
            } else {
                inQuotes = false;
            }
            continue;
        }

        if (!inQuotes && ch === delimiter) {
            fields.push(current);
            current = "";
            continue;
        }

        current += ch;
    }
    fields.push(current);

    return fields.map((f) => f.trim());
}

window.vfpsCsvUpload = {
    getSelectedFileName: function (inputElementId) {
        const input = document.getElementById(inputElementId);
        return input && input.files && input.files.length > 0 ? input.files[0].name : null;
    },

    readHeaderRow: async function (inputElementId, delimiter) {
        const input = document.getElementById(inputElementId);
        if (!input || !input.files || input.files.length === 0) {
            return [];
        }

        const file = input.files[0];
        // The header row is always near the start of the file - a small prefix is enough even
        // for multi-GB files, and avoids reading the whole thing just to show column names.
        const chunk = await file.slice(0, 65536).text();
        const newlineIndex = chunk.indexOf("\n");
        const firstLine = (newlineIndex === -1 ? chunk : chunk.slice(0, newlineIndex)).replace(
            /\r$/,
            ""
        );

        if (!firstLine) {
            return [];
        }

        return parseCsvLine(firstLine, delimiter || ",");
    },

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
