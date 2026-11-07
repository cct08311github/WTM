/**
 * References for the file API (/api/_file/*).
 *
 * The server writes a short-lived reference ("capability") next to the data that names a file: `Field__fref` in list rows, `FileRefs`
 * (file id -> reference) on a record, `Ref` in the answer of an upload. An <img> cannot send the bearer token, so the reference travels in the
 * address (`_DONOT_USE_FREF`). `DeleteRef` of an upload is the only thing that lets this user delete that file again. `LinkRef` is the proof that
 * this user uploaded the file: a form that saves a record posts it as `UploadedFileRefs` (see `withLinkRefs`), without it the server does not link a file
 * that the record did not already refer to.
 * Nothing is persisted: the registry is memory only, capped, and cleared when the session is cleared (logout).
 */
const MAX = 2000;
const refs = new Map<string, string>();
const deleteRefs = new Map<string, string>();
const linkRefs = new Map<string, string>();

const norm = (id: any) => (id === null || id === undefined ? '' : String(id).toLowerCase());

const put = (map: Map<string, string>, id: any, ref: any) => {
	const key = norm(id);
	if (!key || !ref || typeof ref !== 'string') return;
	map.delete(key);
	map.set(key, ref);
	while (map.size > MAX) {
		const first = map.keys().next();
		if (first.done) break;
		map.delete(first.value);
	}
};

export function remember(id: any, ref: any) {
	put(refs, id, ref);
}

export function refFor(id: any): string | undefined {
	return refs.get(norm(id));
}

export function rememberDelete(id: any, ref: any) {
	put(deleteRefs, id, ref);
}

export function deleteRefFor(id: any): string | undefined {
	return deleteRefs.get(norm(id));
}

export function forgetDelete(id: any) {
	deleteRefs.delete(norm(id));
}

const GUID = /[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/gi;

/**
 * After a form was saved, the files it names belong to the saved record: the delete references of the uploads in it are dropped, so a reference from
 * an earlier save cannot delete a file that is now in use. `body` is the request body (a JSON string or an object).
 */
export function forgetDeleteIn(body: any) {
	if (deleteRefs.size === 0 || body === undefined || body === null) return;
	const text = typeof body === 'string' ? body : JSON.stringify(body);
	(text.match(GUID) || []).forEach((id) => forgetDelete(id));
}

/** The `LinkRef` of an upload: the proof, posted with the form that saves the file into a record. */
export function rememberLink(id: any, ref: any) {
	put(linkRefs, id, ref);
}

export function linkRefFor(id: any): string | undefined {
	return linkRefs.get(norm(id));
}

/**
 * The body of a save request with `UploadedFileRefs`: the link references of the uploads that the body names (by file id). The body is a JSON
 * string or an object (not copied into, not changed); anything else (FormData, an array, a body without a known upload) is returned as it is.
 */
export function withLinkRefs(body: any): any {
	if (linkRefs.size === 0 || body === undefined || body === null) return body;
	const isText = typeof body === 'string';
	if (!isText && (typeof body !== 'object' || Array.isArray(body) || (typeof FormData !== 'undefined' && body instanceof FormData))) return body;
	const text = isText ? body : JSON.stringify(body);
	const found: string[] = [];
	(text.match(GUID) || []).forEach((id) => {
		const ref = linkRefFor(id);
		if (ref && found.indexOf(ref) < 0) found.push(ref);
	});
	if (found.length === 0) return body;
	try {
		const record = isText ? JSON.parse(body) : { ...body };
		if (!record || typeof record !== 'object' || Array.isArray(record)) return body;
		const existing = Array.isArray(record.UploadedFileRefs) ? record.UploadedFileRefs : [];
		record.UploadedFileRefs = existing.concat(found.filter((x) => existing.indexOf(x) < 0));
		return isText ? JSON.stringify(record) : record;
	} catch (e) {
		return body;
	}
}

/** After a form was saved, the uploads it names are part of the record: their link references are dropped. */
export function forgetLinkIn(body: any) {
	if (linkRefs.size === 0 || body === undefined || body === null) return;
	const text = typeof body === 'string' ? body : JSON.stringify(body);
	(text.match(GUID) || []).forEach((id) => linkRefs.delete(norm(id)));
}

export function clearFileRefs() {
	refs.clear();
	deleteRefs.clear();
	linkRefs.clear();
}

/** Takes the references out of a response: `FileRefs` of a record and `<Field>__fref` of the rows of a list. */
export function collect(data: any) {
	if (!data || typeof data !== 'object') return;
	if (data.FileRefs && typeof data.FileRefs === 'object') {
		Object.keys(data.FileRefs).forEach((id) => remember(id, data.FileRefs[id]));
	}
	const rows = Array.isArray(data.Data) ? data.Data : Array.isArray(data) ? data : [];
	const visit = (row: any) => {
		if (!row || typeof row !== 'object') return;
		Object.keys(row).forEach((key) => {
			if (key.endsWith('__fref')) {
				remember(row[key.substring(0, key.length - 6)], row[key]);
			}
		});
		if (Array.isArray(row.children)) row.children.forEach(visit);
	};
	rows.forEach(visit);
}

const query = (params: Record<string, any>) => {
	const parts: string[] = [];
	Object.keys(params).forEach((k) => {
		const v = params[k];
		if (v !== undefined && v !== null && v !== '') parts.push(`${k}=${encodeURIComponent(v)}`);
	});
	return parts.length ? '?' + parts.join('&') : '';
};

/** Address of a file for an <img>/<a>; without a known reference it is the bare address (which works while FileEndpoints:Mode is Audit). */
export function fileUrl(id: any, opt: { width?: any; height?: any } = {}) {
	return `/api/_file/GetFile/${encodeURIComponent(id)}` + query({ width: opt.width, height: opt.height, _DONOT_USE_FREF: refFor(id) });
}

export function downloadUrl(id: any) {
	return `/api/_file/DownloadFile/${encodeURIComponent(id)}` + query({ _DONOT_USE_FREF: refFor(id) });
}

/** Address of the photo of a user: with a known reference the file itself, otherwise the photo endpoint (shown to everybody for a real user photo). */
export function userPhotoUrl(id: any, opt: { width?: any; height?: any } = {}) {
	if (refFor(id)) {
		return fileUrl(id, opt);
	}
	return `/api/_file/GetUserPhoto/${encodeURIComponent(id)}` + query({ width: opt.width, height: opt.height });
}

/** Query parameters that name the reference of a file, for requests made through axios. */
export function refParams(id: any): Record<string, any> {
	const r = refFor(id);
	return r ? { _DONOT_USE_FREF: r } : {};
}

export default { forgetDeleteIn, rememberLink, linkRefFor, withLinkRefs, forgetLinkIn, remember, refFor, rememberDelete, deleteRefFor, forgetDelete, clearFileRefs, collect, fileUrl, downloadUrl, refParams };
