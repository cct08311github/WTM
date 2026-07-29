<template>
  <el-upload ref="uploadRef" action="/api/_file/Upload" :on-success="onSuccess" :headers="header"
    v-model:file-list="files" :show-file-list="multi" list-type="picture-card" :disabled="props.disabled" :multiple="props.multi">
    <div v-if="imageUrl && multi !== true">
      <el-image :src="imageUrl" fit="fill"
        :preview-src-list="props.disabled === true && imageUrl ? [imageUrl] : undefined" />
    </div>
    <i v-else-if="disabled!==true" class="fa fa-plus"></i>
    <span v-else>{{$t("message._system.uploadFile.total",{count:files?.length})}}</span>
    <el-image-viewer teleported v-if="previewOpen" :url-list="previewUrlList" :initial-index="previewIndex"
     infinite hide-on-click-modal @close="previewOpen=false"></el-image-viewer>
    <template #file="{ file }">
      <div class="el-upload-list--picture-card">
        <img class="el-upload-list__item-thumbnail" :src="file.url" />
        <span class="el-upload-list__item-actions">
          <span class="el-upload-list__item-delete" @click.stop="onPreview(file)">
            <i class="fa fa-eye"></i>
          </span>
          <span  class="el-upload-list__item-delete" @click.stop="onDownload(file)">
            <i class="fa fa-download"></i>
          </span>
          <span v-if="!props.disabled" class="el-upload-list__item-delete" @click.stop="onRemove(file)">
            <i class="fa fa-trash"></i>
          </span>
        </span>
      </div>
    </template>
  </el-upload>
</template>

<script setup lang="ts">

import { ref, computed, watch, onUnmounted } from 'vue';
import { Local } from '/@/utils/storage';
import fileApi from '/@/api/file';

// #830 review round 3: fileApi().getFile() hands back a blob: object URL
// (URL.createObjectURL), which the browser holds in memory until explicitly revoked or the
// document unloads. Every call site below that replaces a previously-fetched URL revokes the
// OLD one first -- never one still bound to a visible <img>/el-image, only the value being
// thrown away -- so a form field that cycles through many records doesn't accumulate one blob
// per record for the lifetime of the page.
const revokeIfBlob = (url: string | null | undefined) => {
  if (url && url.startsWith('blob:')) {
    URL.revokeObjectURL(url);
  }
};

const emit = defineEmits(['refresh', 'update:modelValue', 'update:deletedFiles']);
const uploadRef = ref();
// 定义父组件传过来的值
const props = defineProps({
  multi: Boolean,
  disabled: Boolean,
  modelValue: null,
  deletedFiles: null
});

const filevalue = computed({
  get() {
    return props.modelValue;
  },
  set(value) {
    emit('update:modelValue', value)
  }
})

const deletedFiles = computed<any[]>({
  get() {
    return props.deletedFiles ?? [];
  },
  set(value) {
    emit('update:deletedFiles', value)
  }
});

const previewOpen = ref(false);
const previewIndex = ref(0);
const files = ref();
const imageUrl = ref('');
// #830 review finding 2: fetched lazily (only when the viewer is actually opened) via
// fileApi().getFile(), which does an authenticated axios request and returns an object URL --
// a plain '/api/_file/getfile/'+FileId string can no longer be bound directly to an <img>/
// el-image src now that GetFile requires auth (a native browser image fetch carries neither
// the Bearer header nor a cookie).
const previewUrlList = ref<string[]>([]);
const header = computed(() => {
  return { Authorization: `Bearer ${Local.get('token')}` }
})
watch(filevalue, async () => {

if(!filevalue.value){
  // #856: clearing modelValue used to only reset `files`, leaving `imageUrl` and
  // `previewUrlList` pointing at blob: URLs nothing would ever revoke -- before blob URLs
  // this was just a stale preview, now it is a real memory leak plus a capability
  // (createObjectURL access to that file's bytes) that outlives the value it was for.
  revokeIfBlob(imageUrl.value);
  imageUrl.value = '';
  previewUrlList.value.forEach(revokeIfBlob);
  previewUrlList.value = [];
  (files.value ?? []).forEach((item: any) => revokeIfBlob(item.url));
  files.value = [];
  return;
}

if(!files.value){
  files.value = [];
}

if (props.multi == false) {

  let fv = null;
  if (files.value && files.value.length > 0) {
    fv = files.value[0].FileId;
  }
  if (props.modelValue !== fv) {
    revokeIfBlob(imageUrl.value);
    imageUrl.value = "";
    files.value = [];
    if (props.modelValue) {
      imageUrl.value = "empty";
      let filename = await fileApi().getName(props.modelValue);
      let localUrl = await fileApi().getFile(props.modelValue, 150, 150);
      files.value = [{ name: filename, url: localUrl, FileId: props.modelValue }];
      imageUrl.value = localUrl;
    }
  }
}
else{
  for(let i = 0;i<props.modelValue.length;i++){
    if(files.value.length<= i || files.value[i].FileId != props.modelValue[i].FileId){
      let filename = await fileApi().getName(props.modelValue[i].FileId);
      let localUrl = await fileApi().getFile(props.modelValue[i].FileId, 150, 150);
      let nv =
        {
          name: filename,
          url: localUrl,
          FileId: props.modelValue[i].FileId,
          keyID: props.modelValue[i].ID,
          index:i
        };
      if(files.value.length <=i ){
        files.value.push(nv);
      }
      else{
        revokeIfBlob(files.value[i].url);
        files.value[i] = nv;
      }
    }
  }
  if(files.value.length > props.modelValue.length){
    for (let i = props.modelValue.length; i < files.value.length; i++) {
      revokeIfBlob(files.value[i]?.url);
    }
    files.value.splice(props.modelValue.length,props.modelValue.length-files.value.length);
  }
}
})

const onDownload = (file: any) => {
  fileApi().downloadFile(file.FileId);
}
const onPreview = async (file: any) => {
  // Full-resolution (no width/height) blob URLs for the full-screen viewer, fetched only when
  // it is actually opened -- separate from the 150x150 thumbnail URLs already held on
  // files.value[].url. Revoke the previous batch (from the last time the viewer was opened)
  // before fetching a new one, rather than accumulating one set per open.
  previewUrlList.value.forEach(revokeIfBlob);
  previewUrlList.value = await Promise.all(files.value.map((item: any) => fileApi().getFile(item.FileId)));
  previewIndex.value = file.index;
  previewOpen.value = true;
}
const onSuccess = (res: any, uploadFile: any, uploadFiles: any) => {
  uploadFile.FileId = res.Id;
  if (props.multi == false) {
    filevalue.value = res.Id;
    revokeIfBlob(imageUrl.value);
    imageUrl.value = URL.createObjectURL(uploadFile.raw!);
    if (files.value.length > 1) {
      let nv = [...deletedFiles.value];
      deletedFiles.value.push(files.value[0].FileId);
      files.value.splice(0, 1);
    }
  }
  else{
    let tempfv = [];
    tempfv = uploadFiles.map((item:any,i:number)=>{
      item.index = i;
      return {FileId : item.FileId, index:i};
    });
    filevalue.value = tempfv;
  }
}

const onRemove = (file: any) => {
  fileApi().deleteFile(file.FileId);
  const f = files.value;
  if (f) {
    for (let index = 0; index < f.length; index++) {
      const item = files.value[index];
      if (item.FileId === file.FileId) {
        let nv = [...deletedFiles.value];
        deletedFiles.value.push(file.FileId);
        revokeIfBlob(item.url);
        f.splice(index, 1);
        index--;
      }
    }
  }
  filevalue.value = f.map((item:any,i:number)=>{item.index=i; return {FileId: item.FileId, ID: item.keyID, index:i}});
}

// #830 review round 3: revoke every blob: URL this instance is still holding when the
// component is destroyed (e.g. navigating away from the form) -- otherwise whatever was on
// screen at that moment leaks for the rest of the page's lifetime.
onUnmounted(() => {
  revokeIfBlob(imageUrl.value);
  (files.value ?? []).forEach((item: any) => revokeIfBlob(item.url));
  previewUrlList.value.forEach(revokeIfBlob);
});

// 暴露变量
defineExpose({

});
</script>

<style scoped lang="scss"></style>
