<script setup lang="ts">
import { ref, onMounted, onUnmounted, watch, computed, shallowRef, nextTick } from 'vue'
import * as echarts from 'echarts/core'
import { MapChart } from 'echarts/charts'
import { GeoComponent, TooltipComponent, VisualMapComponent } from 'echarts/components'
import { CanvasRenderer } from 'echarts/renderers'
import { geoTrafficApi } from '@/api'
import { countryNameMap, provinceNameMap, cityToProvinceMap } from '@/utils/geoNameMap'
import type { GeoTrafficItem } from '@/types'

echarts.use([MapChart, GeoComponent, TooltipComponent, VisualMapComponent, CanvasRenderer])

// 基础路径（兼容子目录部署）
const BASE = import.meta.env.BASE_URL

const chartRef = ref<HTMLDivElement>()
const chart = shallowRef<echarts.ECharts>()
const mapMode = ref<'world' | 'china'>('china')
const dataMode = ref<'visits' | 'intercepts'>('visits')
const loading = ref(false)
const mapReady = ref(false)

// 记录已注册的地图
const registeredMaps = new Set<string>()

const countryVisits = ref<GeoTrafficItem[]>([])
const countryIntercepts = ref<GeoTrafficItem[]>([])
const regionVisits = ref<GeoTrafficItem[]>([])
const regionIntercepts = ref<GeoTrafficItem[]>([])

/** 将原始 region 数据归并到省份并排序 */
function aggregateToProvince(raw: GeoTrafficItem[]): GeoTrafficItem[] {
  const agg: Record<string, number> = {}
  for (const item of raw) {
    const mapped = provinceNameMap[item.name] || cityToProvinceMap[item.name] || item.name
    agg[mapped] = (agg[mapped] || 0) + item.value
  }
  return Object.entries(agg)
    .map(([name, value]) => ({ name, value }))
    .sort((a, b) => b.value - a.value)
}

const currentData = computed(() => {
  if (mapMode.value === 'china') {
    const raw = dataMode.value === 'visits' ? regionVisits.value : regionIntercepts.value
    return aggregateToProvince(raw)
  }
  return dataMode.value === 'visits' ? countryVisits.value : countryIntercepts.value
})

const totalCount = computed(() => {
  return currentData.value.reduce((sum, item) => sum + item.value, 0)
})

async function registerMapData(name: string, jsonFile: string) {
  if (registeredMaps.has(name)) return true
  try {
    const res = await fetch(`${BASE}geo/${jsonFile}`)
    if (!res.ok) throw new Error(`HTTP ${res.status}`)
    const geoJson = await res.json()
    echarts.registerMap(name, geoJson as any)
    registeredMaps.add(name)
    return true
  } catch (e) {
    console.error(`加载地图数据失败 [${name}]:`, e)
    return false
  }
}

async function ensureCurrentMap() {
  const name = mapMode.value === 'china' ? 'china' : 'world'
  const file = name === 'china' ? 'china.json' : 'world.json'
  return await registerMapData(name, file)
}

async function loadData() {
  loading.value = true
  try {
    const data = await geoTrafficApi.getStats()
    if (data.success !== false) {
      countryVisits.value = data.countryVisits || []
      countryIntercepts.value = data.countryIntercepts || []
      regionVisits.value = data.regionVisits || []
      regionIntercepts.value = data.regionIntercepts || []
      if (mapReady.value) {
        updateChart()
      }
    }
  } catch (e) {
    console.error('加载地理流量数据失败:', e)
  } finally {
    loading.value = false
  }
}

function buildOption(): echarts.EChartsCoreOption {
  const isChina = mapMode.value === 'china'
  const isIntercepts = dataMode.value === 'intercepts'

  let rawData: GeoTrafficItem[]
  if (isChina) {
    rawData = isIntercepts ? regionIntercepts.value : regionVisits.value
  } else {
    rawData = isIntercepts ? countryIntercepts.value : countryVisits.value
  }

  // 转换名称以匹配 ECharts 地图
  let seriesData: Array<{ name: string; value: number }>
  if (isChina) {
    // 将城市级数据归并到省份：先映射到省份全名，再按省份聚合
    const provinceAgg: Record<string, number> = {}
    for (const item of rawData) {
      // 优先用 provinceNameMap（省份短名→全名），其次用 cityToProvinceMap（城市→省份全名）
      const mapped = provinceNameMap[item.name] || cityToProvinceMap[item.name] || item.name
      provinceAgg[mapped] = (provinceAgg[mapped] || 0) + item.value
    }
    seriesData = Object.entries(provinceAgg).map(([name, value]) => ({ name, value }))
  } else {
    seriesData = rawData.map(item => ({
      name: countryNameMap[item.name] || item.name,
      value: item.value,
    }))
  }

  const maxVal = seriesData.length > 0 ? Math.max(...seriesData.map(d => d.value)) : 100

  return {
    tooltip: {
      trigger: 'item',
      formatter: (params: any) => {
        const val = params.value || 0
        return `<b>${params.name}</b><br/>${isIntercepts ? '拦截' : '访问'}: ${val.toLocaleString()}`
      },
      backgroundColor: 'rgba(20, 25, 35, 0.95)',
      borderColor: '#2d3748',
      textStyle: { color: '#f1f5f9', fontSize: 13 },
    },
    visualMap: {
      min: 0,
      max: maxVal || 100,
      left: 16,
      bottom: 16,
      text: ['高', '低'],
      calculable: true,
      inRange: {
        color: isIntercepts
          ? ['#1e1b2e', '#7c3aed', '#ef4444']
          : ['#1e1b2e', '#3b82f6', '#22d3ee'],
      },
      textStyle: { color: '#94a3b8', fontSize: 12 },
      itemWidth: 12,
      itemHeight: 100,
    },
    series: [{
      name: isIntercepts ? '拦截量' : '访问量',
      type: 'map',
      map: isChina ? 'china' : 'world',
      roam: true,
      zoom: isChina ? 1.2 : 1,
      emphasis: {
        label: { show: true, color: '#f1f5f9', fontSize: 12 },
        itemStyle: { areaColor: '#f59e0b' },
      },
      select: {
        disabled: true,
      },
      itemStyle: {
        areaColor: '#111827',
        borderColor: '#374151',
        borderWidth: 0.5,
      },
      label: {
        show: false,
      },
      data: seriesData,
    }],
  }
}

function updateChart() {
  if (!chart.value || !mapReady.value) return
  chart.value.setOption(buildOption(), true)
}

watch([mapMode, dataMode], async () => {
  const ok = await ensureCurrentMap()
  if (ok) {
    mapReady.value = true
    await nextTick()
    updateChart()
  }
})

function handleResize() {
  chart.value?.resize()
}

let refreshTimer: number | null = null

onMounted(async () => {
  // 先注册默认地图（中国）
  const ok = await ensureCurrentMap()

  if (chartRef.value) {
    chart.value = echarts.init(chartRef.value, undefined, {
      renderer: 'canvas',
    })
  }

  if (ok) {
    mapReady.value = true
  }

  await loadData()
  refreshTimer = window.setInterval(loadData, 60000)
  window.addEventListener('resize', handleResize)
})

onUnmounted(() => {
  if (refreshTimer) clearInterval(refreshTimer)
  chart.value?.dispose()
  window.removeEventListener('resize', handleResize)
})
</script>

<template>
  <div class="card">
    <!-- 头部 -->
    <div class="flex items-center justify-between mb-4 pb-3 border-b border-dark-border">
      <div class="flex items-center gap-3">
        <h2 class="text-base font-semibold text-gray-100">地理流量分布</h2>
        <span class="text-xs text-gray-500">
          {{ dataMode === 'visits' ? '总访问' : '总拦截' }}: {{ totalCount.toLocaleString() }}
        </span>
      </div>
      <div class="flex items-center gap-2">
        <!-- 地图模式切换 -->
        <div class="flex rounded-md overflow-hidden border border-dark-border">
          <button
            @click="mapMode = 'world'"
            :class="['px-3 py-1 text-xs transition-colors',
              mapMode === 'world'
                ? 'bg-blue-600 text-white'
                : 'bg-dark-card text-gray-400 hover:text-gray-200']"
          >世界</button>
          <button
            @click="mapMode = 'china'"
            :class="['px-3 py-1 text-xs transition-colors',
              mapMode === 'china'
                ? 'bg-blue-600 text-white'
                : 'bg-dark-card text-gray-400 hover:text-gray-200']"
          >中国</button>
        </div>
        <!-- 数据模式切换 -->
        <div class="flex rounded-md overflow-hidden border border-dark-border">
          <button
            @click="dataMode = 'visits'"
            :class="['px-3 py-1 text-xs transition-colors',
              dataMode === 'visits'
                ? 'bg-blue-600 text-white'
                : 'bg-dark-card text-gray-400 hover:text-gray-200']"
          >访问</button>
          <button
            @click="dataMode = 'intercepts'"
            :class="['px-3 py-1 text-xs transition-colors',
              dataMode === 'intercepts'
                ? 'bg-blue-600 text-white'
                : 'bg-dark-card text-gray-400 hover:text-gray-200']"
          >仅拦截</button>
        </div>
      </div>
    </div>

    <!-- 内容区：地图 + 排行 -->
    <div class="flex gap-4">
      <!-- 地图 -->
      <div class="flex-1 min-h-[420px]" ref="chartRef"></div>

      <!-- 排行榜 -->
      <div class="w-56 shrink-0 flex flex-col">
        <h3 class="text-sm text-gray-400 mb-3">
          {{ mapMode === 'china' ? '省份排行' : '国家/地区排行' }}
        </h3>
        <div class="flex-1 space-y-1.5 max-h-[390px] overflow-y-auto pr-1">
          <div
            v-for="(item, index) in currentData.slice(0, 20)"
            :key="item.name"
            class="flex items-center gap-2 text-sm group"
          >
            <span
              :class="[
                'w-5 h-5 flex items-center justify-center rounded text-xs font-bold shrink-0',
                index < 3
                  ? 'bg-blue-500/20 text-blue-400'
                  : 'bg-dark-bg text-gray-500'
              ]"
            >{{ index + 1 }}</span>
            <span class="flex-1 text-gray-300 truncate text-xs">{{ item.name }}</span>
            <span class="text-gray-500 font-mono text-xs shrink-0">{{ item.value.toLocaleString() }}</span>
          </div>
          <div v-if="currentData.length === 0" class="text-gray-500 text-xs text-center py-10">
            暂无数据
          </div>
        </div>
      </div>
    </div>
  </div>
</template>
