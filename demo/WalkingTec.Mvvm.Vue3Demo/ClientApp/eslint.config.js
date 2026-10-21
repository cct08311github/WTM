// ESLint 9 flat config (migrated from .eslintrc.js / .eslintignore, #164).
// Rule intent is unchanged: eslint:recommended + eslint-plugin-vue "essential" presets,
// with the project's rule overrides below.
const js = require('@eslint/js');
const globals = require('globals');
const vue = require('eslint-plugin-vue');
const vueParser = require('vue-eslint-parser');
const tsParser = require('@typescript-eslint/parser');
const tsPlugin = require('@typescript-eslint/eslint-plugin');

module.exports = [
	// Former .eslintignore. Its patterns had no leading slash, so they matched at any depth.
	{
		ignores: [
			'**/*.sh',
			'**/node_modules/**',
			'**/lib/**',
			'**/*.md',
			'**/*.scss',
			'**/*.woff',
			'**/*.ttf',
			'**/.vscode/**',
			'**/.idea/**',
			'**/dist/**',
			'**/mock/**',
			'**/public/**',
			'**/bin/**',
			'**/build/**',
			'**/config/**',
			'index.html',
			'src/assets/**',
		],
	},

	js.configs.recommended,
	// Previously: 'plugin:vue/vue3-essential' and 'plugin:vue/essential' (the Vue 2 preset), both kept.
	// In eslint-plugin-vue 10 the Vue 3 preset is named 'essential' and the Vue 2 one 'vue2-essential'.
	...vue.configs['flat/essential'],
	...vue.configs['flat/vue2-essential'],

	{
		files: ['**/*.{js,jsx,ts,tsx,vue}'],
		languageOptions: {
			ecmaVersion: 2021,
			sourceType: 'module',
			globals: { ...globals.browser, ...globals.es2021, ...globals.node },
			parser: vueParser,
			parserOptions: {
				ecmaVersion: 2021,
				sourceType: 'module',
				parser: tsParser,
			},
		},
		plugins: { '@typescript-eslint': tsPlugin },
		// eslint 9 reports unused "eslint-disable" comments by default; eslint 8 did not.
		linterOptions: { reportUnusedDisableDirectives: 'off' },
		rules: {
			// http://eslint.cn/docs/rules/
			// https://eslint.vuejs.org/rules/
			// https://typescript-eslint.io/rules/no-unused-vars/
			// '@typescript-eslint/ban-ts-ignore', 'ban-types' and 'explicit-module-boundary-types' were set to
			// 'off' here; those rules no longer exist in typescript-eslint 8, so the entries are dropped.
			'@typescript-eslint/explicit-function-return-type': 'off',
			'@typescript-eslint/no-explicit-any': 'off',
			'@typescript-eslint/no-var-requires': 'off',
			'@typescript-eslint/no-empty-function': 'off',
			'@typescript-eslint/no-use-before-define': 'off',
			'@typescript-eslint/ban-ts-comment': 'off',
			'@typescript-eslint/no-non-null-assertion': 'off',
			'@typescript-eslint/no-redeclare': 'error',
			'@typescript-eslint/no-non-null-asserted-optional-chain': 'off',
			// eslint 9 / typescript-eslint 8 default caughtErrors to 'all'; keep the old behaviour ('none').
			'@typescript-eslint/no-unused-vars': ['error', { caughtErrors: 'none' }],
			'vue/custom-event-name-casing': 'off',
			'vue/attributes-order': 'off',
			'vue/one-component-per-file': 'off',
			'vue/html-closing-bracket-newline': 'off',
			'vue/max-attributes-per-line': 'off',
			'vue/multiline-html-element-content-newline': 'off',
			'vue/singleline-html-element-content-newline': 'off',
			'vue/attribute-hyphenation': 'off',
			'vue/html-self-closing': 'off',
			'vue/no-multiple-template-root': 'off',
			'vue/require-default-prop': 'off',
			'vue/no-v-model-argument': 'off',
			'vue/no-arrow-functions-in-watch': 'off',
			'vue/no-template-key': 'off',
			'vue/no-v-html': 'off',
			'vue/comment-directive': 'off',
			'vue/no-parsing-error': 'off',
			'vue/no-deprecated-v-on-native-modifier': 'off',
			'vue/multi-word-component-names': 'off',
			'no-useless-escape': 'off',
			'no-sparse-arrays': 'off',
			'no-prototype-builtins': 'off',
			'no-constant-condition': 'off',
			'no-use-before-define': 'off',
			'no-restricted-globals': 'off',
			'no-restricted-syntax': 'off',
			'generator-star-spacing': 'off',
			'no-unreachable': 'off',
			'no-unused-vars': ['error', { caughtErrors: 'none' }],
			'no-case-declarations': 'off',
			'no-console': 'error',
			'no-redeclare': 'off',
		},
	},
	{
		files: ['**/*.ts', '**/*.tsx', '**/*.vue'],
		rules: {
			'no-undef': 'off',
		},
	},
];
