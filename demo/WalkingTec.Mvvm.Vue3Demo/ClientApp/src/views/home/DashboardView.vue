<template>
  <div class="dashboard-view">
    <!-- Toolbar -->
    <div class="dashboard-toolbar">
      <el-button-group>
        <el-button :type="currentDevice === 'lg' ? 'primary' : ''" @click="setDevice('lg')">
          <el-icon><Monitor /></el-icon> 桌面
        </el-button>
        <el-button :type="currentDevice === 'md' ? 'primary' : ''" @click="setDevice('md')">
          <el-icon><Iphone /></el-icon> 平板
        </el-button>
        <el-button :type="currentDevice === 'sm' ? 'primary' : ''" @click="setDevice('sm')">
          <el-icon><Cellphone /></el-icon> 小平板
        </el-button>
        <el-button :type="currentDevice === 'xs' ? 'primary' : ''" @click="setDevice('xs')">
          <el-icon><Cellphone /></el-icon> 手機
        </el-button>
      </el-button-group>
    </div>

    <!-- Preview Container -->
    <div class="preview-container" :class="`preview-${currentDevice}`">
      <div class="device-frame" :style="deviceFrameStyle">
        <div class="dashboard-content">
          <el-row :gutter="12">
            <el-col
              v-for="item in layoutItems"
              :key="item.id"
              v-bind="getColProps(item)"
            >
              <div class="widget-card">
                <div class="widget-header">{{ getWidgetTitle(item.id) }}</div>
                <div class="widget-body">
                  <!-- Widget content would go here -->
                  <p>Widget: {{ item.id }}</p>
                  <p class="debug-info">
                    span={{ getSpanForDevice(item, currentDevice) }}
                    <span v-if="item[currentDevice]">(override)</span>
                  </p>
                </div>
              </div>
            </el-col>
          </el-row>
        </div>
      </div>
    </div>
  </div>
</template>

<script lang="ts" setup>
import { ref, computed } from 'vue'
import { Monitor, Iphone, Cellphone } from '@element-plus/icons-vue'
import type { DeviceType, LayoutItem } from '/@/utils/dashboard/responsive'
import { getSpanForDevice, getColProps, previewDeviceWidths, getDeviceLabel } from '/@/utils/dashboard/responsive'

// Demo layout items with responsive breakpoints
const layoutItems = ref<LayoutItem[]>([
  { id: 'widget1', x: 0, y: 0, w: 3, h: 1, lg: 3, md: 4, sm: 6, xs: 12 },
  { id: 'widget2', x: 3, y: 0, w: 3, h: 1, lg: 3, md: 4, sm: 6, xs: 12 },
  { id: 'widget3', x: 6, y: 0, w: 3, h: 1, lg: 3, md: 4, sm: 6, xs: 12 },
  { id: 'widget4', x: 9, y: 0, w: 3, h: 1, lg: 3, md: 12, sm: 12, xs: 24 },
  { id: 'widget5', x: 0, y: 1, w: 6, h: 2, lg: 6, md: 8, sm: 12, xs: 24 },
  { id: 'widget6', x: 6, y: 1, w: 6, h: 2, lg: 6, md: 4, sm: 12, xs: 24 },
])

const currentDevice = ref<DeviceType>('lg')

// Demo widget titles
const widgetTitles: Record<string, string> = {
  widget1: '銷售統計',
  widget2: '用戶增長',
  widget3: '訂單處理',
  widget4: '系統狀態',
  widget5: '營收趨勢',
  widget6: '最新活動'
}

function getWidgetTitle(id: string): string {
  return widgetTitles[id] || id
}

function setDevice(device: DeviceType) {
  currentDevice.value = device
}

const deviceFrameStyle = computed(() => ({
  maxWidth: `${previewDeviceWidths[currentDevice.value]}px`
}))
</script>

<style lang="scss" scoped>
.dashboard-view {
  padding: 20px;
}

.dashboard-toolbar {
  margin-bottom: 20px;
  display: flex;
  justify-content: center;
}

.preview-container {
  display: flex;
  justify-content: center;
  padding: 20px;
  background: #f5f7fa;
  border-radius: 8px;
  min-height: 600px;
  
  &.preview-xs {
    background: #e8f5e9;
  }
  &.preview-sm {
    background: #e3f2fd;
  }
  &.preview-md {
    background: #fff3e0;
  }
  &.preview-lg {
    background: #f5f7fa;
  }
}

.device-frame {
  width: 100%;
  background: white;
  border-radius: 8px;
  box-shadow: 0 2px 12px rgba(0, 0, 0, 0.1);
  overflow: hidden;
}

.dashboard-content {
  padding: 16px;
}

.widget-card {
  background: white;
  border: 1px solid #e4e7ed;
  border-radius: 4px;
  margin-bottom: 12px;
  min-height: 80px;
  
  .widget-header {
    padding: 8px 12px;
    background: #f5f7fa;
    border-bottom: 1px solid #e4e7ed;
    font-weight: 500;
  }
  
  .widget-body {
    padding: 12px;
    
    .debug-info {
      font-size: 12px;
      color: #909399;
      margin-top: 8px;
    }
  }
}
</style>
