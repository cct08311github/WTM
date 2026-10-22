import request from '/@/utils/request';
import other from '/@/utils/other';
import { refParams, forgetDelete, deleteRefFor } from '/@/utils/fileRefs';

/**
 * 文件api接口集合
 * @method getFile 获取文件，返回内部url
 */
export default function fileApi(){
	return {
		getFile: (id: any,width:any=null, height:any=null) => {
			return request<any,Blob>({
				responseType: "blob",
				url: '/api/_file/GetFile/'+id,
				method: 'get',
				params:{width,height,...refParams(id)}
			}).then(res=>{
				let rv =URL.createObjectURL(res);
				return rv;
			}).catch(err=>{
				return "";
			});
		},
		getName: (id: any) => {
			return request<any,Blob>({
				url: '/api/_file/GetFileName/'+id,
				method: 'get',
				params: refParams(id)
			}).then(res=>{				
				return res;
			}).catch(err=>{
				return "";
			});
		},
		downloadFile: (id: any) => {
			return request<any,Blob>({
				responseType: "blob",
				url: '/api/_file/DownloadFile/'+id,
				method: 'get',
				params: refParams(id)
			}).then((data)=>{other.downloadFile(data)});
		},

		/** Deletes a file this session uploaded (needs its DeleteRef); a file that was not uploaded here is not deleted, the caller only clears its field. */
		deleteFile:(id:string)=>{
			const ref = deleteRefFor(id);
			if(!ref){
				return Promise.resolve(false);
			}
			return request({
				url:'/api/_file/DeletedFile/'+id,
				method:'delete',
				params:{_DONOT_USE_FREF:ref}
			}).then((rv:any)=>{forgetDelete(id);return rv;});
		}
	};
}
