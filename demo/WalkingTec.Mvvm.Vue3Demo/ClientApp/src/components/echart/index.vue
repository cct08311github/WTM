<template>
    <div v-loading="loading" style="height: 100%;width:100%" ref="chartRef"></div>
</template>
<script setup lang="ts">
import { defineAsyncComponent, reactive,markRaw , getCurrentInstance, onMounted, watch, nextTick, onActivated, ref, computed } from 'vue';
import * as echarts from 'echarts';
import request from '/@/utils/request';

const ci = getCurrentInstance() as any;
const chartRef = ref();
const loading = ref(false);
const props = defineProps({
    type: String,
    title: {
        type:String,
        default:''
    },
    namex: {
        type:String,
        default:''
    },
    namey: {
        type:String,
        default:''
    },
    namecategory: {
        type:String,
        default:''
    },
    request: {
        type: Function,
        default: ()=>{},
    },
    para: {
        type: Object,
        default: { },
    },
    nameaddition: {
        type: String,
        default: '',
    },
    opensmooth: {
        type: Boolean,
        default: false,
    },
    showlegent: {
        type: Boolean,
        default: true,
    },
    showtooltip: {
        type: Boolean,
        default: true,
    },
    ishorizontal: {
        type: Boolean,
        default: false,
    }
});
   
    let chart = null as any;
    let baseOption = {} as any;

// 后端 ToChartData 以字符串形式返回 scatter 系列的 symbolSize（形如
// "function(data) {return data[2] / (max / radius)}"）。这里只按固定格式取出两个数字，
// 在前端还原成固定的函数，不执行响应中的任何内容；格式不符则忽略该字段。
const SYMBOL_SIZE_PATTERN = /^function\(data\)\s*\{return data\[2\]\s*\/\s*\(\s*(-?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?)\s*\/\s*(\d+(?:\.\d+)?)\s*\)\s*\}$/;
const restoreSymbolSize = (raw: any) => {
    if (typeof raw === 'number') {
        return raw;
    }
    const m = typeof raw === 'string' ? SYMBOL_SIZE_PATTERN.exec(raw.trim()) : null;
    if (!m) {
        return undefined;
    }
    const max = Number(m[1]);
    const radius = Number(m[2]);
    return (data: any) => data[2] / (max / radius);
};

// 按 type 属性为 series 中占位的 charttype 填入实际图表类型
const seriesTypeOption = () => {
    const op = {} as any;
    if (props.type == 'piehollow') {
        op.type = 'pie';
        op.radius = ['40%', '70%'];
    } else {
        op.type = props.type;
    }
    if (props.type == 'line') {
        op.smooth = props.opensmooth;
    }
    return op;
};

const doSearch = (api: any = null, para: any = null) => {
    loading.value = true;
    if (!api) {
        api = props.request();
    }
    if (!para) {
        para = props.para;
    }
    request({
        url: api,
        method: 'post',
        data: para,
    }).then((res: any) => {
        const requestOption = Object.assign({}, res) as any;
        requestOption.dataset = JSON.parse(requestOption.dataset);
        requestOption.series = JSON.parse(requestOption.series);
        requestOption.legend = JSON.parse(requestOption.legend);
        if (Array.isArray(requestOption.series)) {
            const typeOption = seriesTypeOption();
            requestOption.series = requestOption.series.map((item: any) => {
                const s = Object.assign({}, item);
                if (s.type === 'charttype') {
                    Object.assign(s, typeOption);
                }
                const symbolSize = restoreSymbolSize(s.symbolSize);
                if (symbolSize === undefined) {
                    delete s.symbolSize;
                } else {
                    s.symbolSize = symbolSize;
                }
                return s;
            });
        }
        const op = Object.assign({}, requestOption, baseOption);
        chart.setOption(op);
        loading.value = false;
        return true;
    })
}
const setBase = () => {
    const op = {} as any;
    if (props.title != undefined) {
        op.title = { text: props.title };
    }
    if (props.showlegent == false) {
        op.legend = { show: false };
    }
    if (props.showtooltip == true) {
        if (props.type == "scatter") {
            op.tooltip = {
                formatter: (params: any) => {
                    // 数据来自后端，输出前做 HTML 转义
                    const enc = (v: any) => echarts.format.encodeHTML(String(v));
                    return enc(params.seriesName) + ' <br/>'
                        + props.namex + enc(params.value[0]) + ' <br/>'
                        + props.namey + enc(params.value[1]) + ' <br/>'
                        + props.nameaddition + enc(params.value[2]) + ' <br/>'
                        + props.namecategory + enc(params.value[3]) + ' <br/>';
                },
            };
        } else if (props.type == "line") {
            op.tooltip = { trigger: 'axis' };
        } else {
            op.tooltip = {};
        }
    }
    if (props.type != "pie" && props.type != "piehollow") {
        if (props.type == "scatter") {
            op.xAxis = { name: props.namex, type: 'value', splitLine: { lineStyle: { type: 'dashed' } } };
            op.yAxis = { name: props.namey, splitLine: { lineStyle: { type: 'dashed' } }, scale: true };
        } else if (props.ishorizontal == false) {
            op.xAxis = { name: props.namex, type: 'category' };
            op.yAxis = { name: props.namey };
        } else {
            op.xAxis = { name: props.namey };
            op.yAxis = { name: props.namex, type: 'category' };
        }
    }
    baseOption = op;
}
onMounted(() => {
    const c = markRaw(echarts.init(chartRef.value));
    chart = c;
    nextTick(() => {
        window.addEventListener('resize', () => { c.resize() });
    });
    setBase();
    doSearch();
})
defineExpose({
	doSearch
});

</script>