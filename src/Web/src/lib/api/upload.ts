// The multipart form every file-ingest surface posts.
//
// Shared by every file-ingest surface rather than copied into each.
//
// Only the FORM is shared. Sending it is `requestMultipart` in `_request.ts`, not a
// per-surface upload helper: it honours an abort signal and handles a 204.
//
// The filename is a parameter rather than a per-format constant because nothing reads
// it — the API's IFormFile binding merely requires one to be present.

export function buildFormData(fields: {
    file: Blob;
    accountId?: string;
    providerAccountId?: string;
}, filename: string): FormData {
    const form = new FormData();
    form.append('file', fields.file, filename);
    if (fields.accountId !== undefined) {
        form.append('accountId', fields.accountId);
    }
    if (fields.providerAccountId !== undefined) {
        form.append('providerAccountId', fields.providerAccountId);
    }
    return form;
}
