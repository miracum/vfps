// Copying a freshly issued access token out of the UI. The secret is shown exactly once, so the
// copy button is the realistic way to get it out of the browser and into wherever it belongs -
// asking someone to select 75 characters of base64url by hand invites a truncated paste.
window.vfpsClipboard = {
  copy: async function (text) {
    // navigator.clipboard is only available in a secure context (HTTPS or localhost). Rather
    // than silently doing nothing on a plain-HTTP deployment, report it so the page can fall
    // back to telling the user to copy the value manually.
    if (!navigator.clipboard) {
      return false;
    }

    try {
      await navigator.clipboard.writeText(text);
      return true;
    } catch {
      return false;
    }
  },
};
