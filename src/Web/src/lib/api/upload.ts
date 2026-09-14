// The multipart form every file-ingest surface posts.
//
// Extracted when the THIRD one landed (Fidelity), which is the condition ofx.ts and
// qif.ts both wrote down and waited for. Two copies were a tolerable duplication;
// three would have been a habit.
//
// Only the FORM is shared. Sending it is `requestMultipart` in `_request.ts`, which
// already existed — qif.ts had grown its own `uploadMultipart` plus a second copy of
// `buildApiError` alongside it, and both are now gone. The shared one is strictly
// better anyway: it honours an abort signal and handles a 204.
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
