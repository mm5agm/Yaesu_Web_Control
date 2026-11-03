// app-version.js - the version the page was served by, for the top strips
// that draw their own page (radio panel, RTTY Contest).

/**
 * The version the page was served by, pre-release suffix and all
 * (<meta name="x-app-version">, as the update banner reads it); '' if none.
 */
export function appVersion(doc = document) {
    return doc.querySelector('meta[name="x-app-version"]')?.content?.trim() ?? '';
}
