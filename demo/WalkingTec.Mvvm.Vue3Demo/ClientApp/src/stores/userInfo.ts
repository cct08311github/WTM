import { defineStore } from 'pinia';
import Cookies from 'js-cookie';
import { Session, Local } from '/@/utils/storage';
import loginapi from '/@/api/login';
import fileapi from '/@/api/file';

/**
 * 用户信息
 * @methods setUserInfos 设置用户信息
 */
export const useUserInfo = defineStore('userInfo', {
	state: (): UserInfosState => ({
		userInfos: {
			userName: '',
			photo: '',
			itCode: '',
			tenantCode: '',
			remoteToken: '',
			currentTenant: '',
			time: 0,
			isDebug: false,
			menu: [],
			roles: [],
			authBtnList: [],
		},
	}),
	actions: {
		async setUserInfos() {
			// 存储用户信息到浏览器缓存
			if (Session.get('userInfo')) {
				const cached = Session.get('userInfo');
				// #830 review round 3: `photo` is a blob: object URL (URL.createObjectURL),
				// which is only valid for the document that created it -- it does NOT survive
				// being read back out of sessionStorage after a reload/new tab, so trusting the
				// cached value here would leave the avatar permanently broken (dead blob: URL)
				// until logout. Re-resolve it from the cached photoId instead. Also revoke
				// whatever blob URL this store currently holds before replacing it -- this only
				// revokes the PREVIOUS value at the point of replacement, never one still being
				// displayed.
				if (cached.photoId) {
					if (this.userInfos.photo && this.userInfos.photo.startsWith('blob:')) {
						URL.revokeObjectURL(this.userInfos.photo);
					}
					cached.photo = await fileapi().getFile(cached.photoId, 25, 25);
				}
				this.userInfos = cached;
			} else {
				const userInfos: any = await this.getApiUserInfo();
				if (userInfos) {
					Session.set('userInfo', userInfos);
					this.userInfos = userInfos;
				}
			}
		},
		async getApiUserInfo() {
			return loginapi()
				.getUserInfo()
				.then(async (res) => {
					const userInfos = {
						userName: res.Name,
						remoteToken: res.RemoteToken ?? '',
						itCode: res.ITCode,
						tenantCode: res.TenantCode ?? '',
						currentTenant: res.CurrentTenant ?? '',
						time: new Date().getTime(),
						photo: '',
						// Cached alongside `photo` so setUserInfos() can re-resolve a fresh blob:
						// URL from a sessionStorage cache hit -- see the comment there.
						photoId: res.PhotoId ?? null,
						isDebug: true,
						roles: [],
						menu: [],
						authBtnList: [],
					};
					if (res.PhotoId !== undefined) {
						// #830 review finding 2: GetFile lost [Public] (correctly -- it was an
						// unauthenticated cross-tenant file read). Vue3 is JWT-only (LoginJwt never
						// calls SignInAsync, so there is no auth cookie) and the Authorization
						// header is attached by the axios interceptor in utils/request.ts -- a raw
						// <img src="..."> the browser fetches natively carries neither the header
						// nor a cookie, so a plain URL string here would 401 on every avatar. Fetch
						// through fileapi().getFile(), which already does an authenticated axios
						// request and hands back an object URL.
						userInfos.photo = await fileapi().getFile(res.PhotoId, 25, 25);
					}
					if (res.Roles !== undefined) {
						userInfos.roles = res.Roles;
					}
					if (res.Attributes?.Actions !== undefined) {
						userInfos.authBtnList = res.Attributes.Actions;
					}
					if (res.Attributes?.IsDebug !== undefined) {
						userInfos.isDebug = res.Attributes.IsDebug;
					}
					if (res.Attributes?.Menus !== undefined) {
						userInfos.menu = res.Attributes.Menus;
					}
					console.log(userInfos);
					return userInfos;
				})
				.catch((error) => {
					Session.clear();
					Local.remove('token');
					window.location.href = '/';
				});
			// return new Promise((resolve) => {
			// 	setTimeout(() => {
			// 		// 模拟数据，请求接口时，记得删除多余代码及对应依赖的引入
			// 		const userName = Cookies.get('userName');
			// 		// 模拟数据
			// 		let defaultRoles: Array<string> = [];
			// 		let defaultAuthBtnList: Array<string> = [];
			// 		// admin 页面权限标识，对应路由 meta.roles，用于控制路由的显示/隐藏
			// 		let adminRoles: Array<string> = ['admin'];
			// 		// admin 按钮权限标识
			// 		let adminAuthBtnList: Array<string> = ['btn.add', 'btn.del', 'btn.edit', 'btn.link'];
			// 		// test 页面权限标识，对应路由 meta.roles，用于控制路由的显示/隐藏
			// 		let testRoles: Array<string> = ['common'];
			// 		// test 按钮权限标识
			// 		let testAuthBtnList: Array<string> = ['btn.add', 'btn.link'];
			// 		// 不同用户模拟不同的用户权限
			// 		if (userName === 'admin') {
			// 			defaultRoles = adminRoles;
			// 			defaultAuthBtnList = adminAuthBtnList;
			// 		} else {
			// 			defaultRoles = testRoles;
			// 			defaultAuthBtnList = testAuthBtnList;
			// 		}
			// 		// 用户信息模拟数据
			// 		const userInfos = {
			// 			userName: userName,
			// 			photo:
			// 				userName === 'admin'
			// 					? 'https://img2.baidu.com/it/u=1978192862,2048448374&fm=253&fmt=auto&app=138&f=JPEG?w=504&h=500'
			// 					: 'https://img2.baidu.com/it/u=2370931438,70387529&fm=253&fmt=auto&app=138&f=JPEG?w=500&h=500',
			// 			time: new Date().getTime(),
			// 			roles: defaultRoles,
			// 			authBtnList: defaultAuthBtnList,
			// 		};
			// 		resolve(userInfos);
			// 	}, 0);
			//});
		},
	},
});
