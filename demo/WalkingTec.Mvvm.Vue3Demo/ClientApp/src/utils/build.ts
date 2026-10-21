import importToCDN from 'vite-plugin-cdn-import';
import type { Plugin } from 'vite';

/**
 * CDN 模式下对应 external 模块的全局变量名（需与 cdn() 的 modules 保持一致）
 */
const cdnGlobals: Record<string, string> = {
	vue: 'Vue',
	'vue-demi': 'VueDemi',
	'vue-router': 'VueRouter',
	'element-plus': 'ElementPlus',
};

/**
 * vite 8 (Rolldown) 对 external 的处理：ESM import 会被 vite-plugin-cdn-import 改写成全局变量，
 * 但被转成 CJS 的依赖（如 vue-i18n.cjs.js、vue-grid-layout）里的 require('vue') 会原样留在产物中，
 * 浏览器没有 require，运行时报 "Calling require for "vue" in an environment that doesn't expose the require function"。
 * 这里在 index.html 里注入一个极小的 require，把 external 模块映射到 CDN 全局变量。
 */
const cdnRequireShim = (): Plugin => ({
	name: 'wtm:cdn-require-shim',
	apply: 'build',
	transformIndexHtml() {
		const code =
			`(function(){var g=${JSON.stringify(cdnGlobals)};` +
			`window.require=function(n){var v=g[n];if(v&&window[v])return window[v];` +
			`throw new Error('Cannot require "'+n+'" in CDN mode: '+(v||'no global')+' is not available');};})();`;
		return [{ tag: 'script', children: code, injectTo: 'head-prepend' }];
	},
});

/**
 * 打包相关
 * 注意 prodUrl：使用的是 jsdelivr 还是 unpkg。它们的 path 可能不一致
 * 文章链接：https://blog.csdn.net/qq_34450741/article/details/129766676，使用的是 jsdelivr
 * @description importToCDN https://github.com/mmf-fe/vite-plugin-cdn-import
 * @description cdn 在线引入的 cdn 地址配置。path：https://www.jsdelivr.com/ || https://unpkg.com/
 * @description external 打包时，过滤包导入。参考：https://rollupjs.org/configuration-options/#external
 */
export const buildConfig = {
	cdn() {
		return [cdnRequireShim(), importToCDN({
			prodUrl: 'https://unpkg.com/{name}@{version}/{path}',
			modules: [
				// autoComplete('vue'),
				// autoComplete('axios'),
				{
					name: 'vue',
					var: 'Vue',
					path: 'dist/vue.global.js',
				},
				{
					name: 'vue-demi',
					var: 'VueDemi',
					path: 'lib/index.iife.js',
				},
				{
					name: 'vue-router',
					var: 'VueRouter',
					path: 'dist/vue-router.global.js',
				},
				{
					name: 'element-plus',
					var: 'ElementPlus',
					path: 'dist/index.full.js',
				},
				// {
				// 	name: '@element-plus/icons-vue',
				// 	var: 'ElementPlusIconsVue',
				// 	path: 'dist/index.iife.min.js',
				// },
				// {
				// 	name: 'echarts',
				// 	var: 'echarts',
				// 	path: 'dist/echarts.min.js',
				// },
				// {
				// 	name: 'vue-i18n',
				// 	var: 'VueI18n',
				// 	path: 'dist/vue-i18n.global.min.js',
				// },
				// {
				// 	name: 'jsplumb',
				// 	var: 'jsPlumb',
				// 	path: 'dist/js/jsplumb.min.js',
				// },
				// {
				// 	name: 'cropperjs',
				// 	var: 'Cropper',
				// 	path: 'dist/cropper.min.js',
				// },
				// {
				// 	name: 'sortablejs',
				// 	var: 'Sortable',
				// 	path: 'Sortable.min.js',
				// },
				// {
				// 	name: 'qrcodejs2-fixes',
				// 	var: 'QRCode',
				// 	path: 'qrcode.min.js',
				// },
				// {
				// 	name: 'print-js',
				// 	var: 'printJS',
				// 	path: 'dist/print.min.js',
				// },
				// {
				// 	name: '@wangeditor/editor',
				// 	var: 'wangEditor',
				// 	path: 'dist/index.min.js',
				// },
				// {
				// 	name: '@wangeditor/editor-for-vue',
				// 	var: 'WangEditorForVue',
				// 	path: 'dist/index.min.js',
				// },
				// {
				// 	name: 'vue-grid-layout',
				// 	var: 'VueGridLayout',
				// 	path: 'https://cdn.jsdelivr.net/npm/vue-grid-layout@3.0.0-beta1/dist/vue-grid-layout.umd.min.js',
				// },
			],
		})];
	},
	external: [
		'vue',
		// 'axios',
		'vue-router',
		'element-plus',
		// '@element-plus/icons-vue',
		// 'echarts',
		// 'vue-i18n',
		// 'jsplumb',
		// 'cropperjs',
		// 'sortablejs',
		// 'qrcodejs2-fixes',
		// 'print-js',
		// '@wangeditor/editor',
		// '@wangeditor/editor-for-vue',
		// 'vue-grid-layout',
	],
};
