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
    return input && input.files && input.files.length > 0
      ? input.files[0].name
      : null;
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
    const firstLine = (
      newlineIndex === -1 ? chunk : chunk.slice(0, newlineIndex)
    ).replace(/\r$/, "");

    if (!firstLine) {
      return [];
    }

    return parseCsvLine(firstLine, delimiter || ",");
  },

  uploadFile: function (inputElementId, presignedUrl, dotNetHelper) {
    const input = document.getElementById(inputElementId);
    if (!input || !input.files || input.files.length === 0) {
      return Promise.reject(new Error("No file selected."));
    }

    const file = input.files[0];

    // Bytes go straight from the browser to S3 via this presigned URL - never through the
    // Blazor circuit or Kestrel, so multi-GB files aren't bounded by SignalR message size.
    // XMLHttpRequest rather than fetch: fetch has no event for outgoing request-body progress,
    // only xhr.upload.onprogress does.
    return new Promise((resolve, reject) => {
      const xhr = new XMLHttpRequest();
      xhr.open("PUT", presignedUrl);
      xhr.setRequestHeader("Content-Type", "text/csv");

      // Throttled rather than forwarding every event: progress fires many times per second for
      // a large file, and each forward is a round trip over the Blazor circuit. The final event
      // (loaded reaches total) always goes through, so the caller's last update is 100%.
      let lastReportedAt = 0;
      xhr.upload.onprogress = (event) => {
        if (!event.lengthComputable || !dotNetHelper) {
          return;
        }
        const now = Date.now();
        const isDone = event.loaded >= event.total;
        if (!isDone && now - lastReportedAt < 200) {
          return;
        }
        lastReportedAt = now;
        dotNetHelper.invokeMethodAsync("OnUploadProgress", event.loaded, event.total);
      };

      xhr.onload = () => {
        if (xhr.status >= 200 && xhr.status < 300) {
          input.value = "";
          resolve();
        } else {
          reject(new Error(`Upload failed with status ${xhr.status}`));
        }
      };

      xhr.onerror = () => reject(new Error("Upload failed due to a network error."));
      xhr.onabort = () => reject(new Error("Upload was aborted."));

      xhr.send(file);
    });
  },
};
