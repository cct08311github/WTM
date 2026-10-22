/**
 * References for the file API (/api/_file/*).
 *
 * The server writes a short-lived reference ("capability") next to the data that names a file: `Field__fref` in list rows, `FileRefs`
 * (file id -> reference) on a record, `Ref` in the answer of an upload. An <img> cannot send the bearer token, so the reference travels in the
 * address (`_DONOT_USE_FREF`). `DeleteRef` of an upload is the only thing that lets this user delete that file again.
 * Nothing is persisted: the registry is memory only, capped, and cleared when the session is cleared (logout).
 */
const MAX = 2000;
const refs = new Map<string, string>();
const deleteRefs = new Map<string, string>();

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

export function clearFileRefs() {
	refs.clear();
	deleteRefs.clear();
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
	return `/api/_file/GetFile/${id}` + query({ width: opt.width, height: opt.height, _DONOT_USE_FREF: refFor(id) });
}

export function downloadUrl(id: any) {
	return `/api/_file/DownloadFile/${id}` + query({ _DONOT_USE_FREF: refFor(id) });
}

/** Query parameters that name the reference of a file, for requests made through axios. */
export function refParams(id: any): Record<string, any> {
	const r = refFor(id);
	return r ? { _DONOT_USE_FREF: r } : {};
}

export default { remember, refFor, rememberDelete, deleteRefFor, forgetDelete, clearFileRefs, collect, fileUrl, downloadUrl, refParams };
