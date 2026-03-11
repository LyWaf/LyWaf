<script setup lang="ts">
import { ref, computed, onMounted, onUnmounted, nextTick } from 'vue'

const props = defineProps<{
  modelValue: string
  placeholder?: string
}>()

const emit = defineEmits<{
  'update:modelValue': [value: string]
}>()

const open = ref(false)
const pickerRef = ref<HTMLElement>()
const inputRef = ref<HTMLElement>()
const panelPos = ref({ top: 0, left: 0 })

// 内部选中状态
const year = ref(2026)
const month = ref(2) // 0-based
const day = ref(1)
const hour = ref(0)
const minute = ref(0)
const second = ref(0)

const pad = (n: number) => n.toString().padStart(2, '0')

const displayValue = computed(() => props.modelValue || '')

// 解析 modelValue 到内部状态
const parseValue = (val: string) => {
  if (!val) {
    const now = new Date()
    year.value = now.getFullYear()
    month.value = now.getMonth()
    day.value = now.getDate()
    hour.value = now.getHours()
    minute.value = now.getMinutes()
    second.value = now.getSeconds()
    return
  }
  // 支持 "2026-03-03 03:00:00" 格式
  const m = val.match(/(\d{4})-(\d{2})-(\d{2})\s+(\d{2}):(\d{2}):(\d{2})/)
  if (m) {
    year.value = parseInt(m[1])
    month.value = parseInt(m[2]) - 1
    day.value = parseInt(m[3])
    hour.value = parseInt(m[4])
    minute.value = parseInt(m[5])
    second.value = parseInt(m[6])
  }
}

const formatResult = () => {
  return `${year.value}-${pad(month.value + 1)}-${pad(day.value)} ${pad(hour.value)}:${pad(minute.value)}:${pad(second.value)}`
}

// 日历数据
const monthNames = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec']
const weekDays = ['Su', 'Mo', 'Tu', 'We', 'Th', 'Fr', 'Sa']

const daysInMonth = computed(() => new Date(year.value, month.value + 1, 0).getDate())
const firstDayOfWeek = computed(() => new Date(year.value, month.value, 1).getDay())

const calendarDays = computed(() => {
  const result: { day: number; current: boolean }[] = []

  // 上月填充
  const prevMonthDays = new Date(year.value, month.value, 0).getDate()
  for (let i = firstDayOfWeek.value - 1; i >= 0; i--) {
    result.push({ day: prevMonthDays - i, current: false })
  }

  // 当月
  for (let i = 1; i <= daysInMonth.value; i++) {
    result.push({ day: i, current: true })
  }

  // 下月填充
  const remaining = 42 - result.length
  for (let i = 1; i <= remaining; i++) {
    result.push({ day: i, current: false })
  }

  return result
})

const isToday = (d: number, current: boolean) => {
  if (!current) return false
  const now = new Date()
  return d === now.getDate() && month.value === now.getMonth() && year.value === now.getFullYear()
}

const isSelected = (d: number, current: boolean) => {
  return current && d === day.value
}

// 导航
const prevYear = () => { year.value-- }
const nextYear = () => { year.value++ }
const prevMonth = () => {
  if (month.value === 0) { month.value = 11; year.value-- }
  else month.value--
}
const nextMonth = () => {
  if (month.value === 11) { month.value = 0; year.value++ }
  else month.value++
}

const selectDay = (d: number, current: boolean) => {
  if (!current) return
  day.value = d
}

// 时间选择
const hours = Array.from({ length: 24 }, (_, i) => i)
const minutes = Array.from({ length: 60 }, (_, i) => i)
const seconds = Array.from({ length: 60 }, (_, i) => i)

const hourRef = ref<HTMLElement>()
const minuteRef = ref<HTMLElement>()
const secondRef = ref<HTMLElement>()

const scrollToTime = () => {
  nextTick(() => {
    const itemH = 28
    if (hourRef.value) hourRef.value.scrollTop = hour.value * itemH
    if (minuteRef.value) minuteRef.value.scrollTop = minute.value * itemH
    if (secondRef.value) secondRef.value.scrollTop = second.value * itemH
  })
}

const selectHour = (h: number) => { hour.value = h }
const selectMinute = (m: number) => { minute.value = m }
const selectSecond = (s: number) => { second.value = s }

// Now 按钮
const setNow = () => {
  const now = new Date()
  year.value = now.getFullYear()
  month.value = now.getMonth()
  day.value = now.getDate()
  hour.value = now.getHours()
  minute.value = now.getMinutes()
  second.value = now.getSeconds()
  scrollToTime()
}

// OK 确认
const confirm = () => {
  emit('update:modelValue', formatResult())
  open.value = false
}

// 清除
const clear = (e: Event) => {
  e.stopPropagation()
  emit('update:modelValue', '')
  open.value = false
}

const panelStyle = computed(() => ({
  top: panelPos.value.top + 'px',
  left: panelPos.value.left + 'px',
}))

const updatePosition = () => {
  if (!inputRef.value) return
  const rect = inputRef.value.getBoundingClientRect()
  panelPos.value = {
    top: rect.bottom + 4,
    left: rect.left,
  }
}

// 打开/关闭
const toggle = () => {
  if (!open.value) {
    parseValue(props.modelValue)
    updatePosition()
    open.value = true
    scrollToTime()
  } else {
    open.value = false
  }
}

// 点击外部关闭
const onClickOutside = (e: MouseEvent) => {
  if (pickerRef.value && !pickerRef.value.contains(e.target as Node) &&
      inputRef.value && !inputRef.value.contains(e.target as Node)) {
    open.value = false
  }
}

onMounted(() => document.addEventListener('mousedown', onClickOutside))
onUnmounted(() => document.removeEventListener('mousedown', onClickOutside))
</script>

<template>
  <div class="dtp-wrapper">
    <!-- 输入框 -->
    <div ref="inputRef" class="dtp-input" @click="toggle">
      <span :class="displayValue ? 'text-gray-200' : 'text-gray-500'">
        {{ displayValue || placeholder || '选择时间' }}
      </span>
      <div class="dtp-input-icons">
        <svg v-if="displayValue" class="dtp-clear" @click="clear" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
          <circle cx="12" cy="12" r="10" /><path d="M15 9l-6 6M9 9l6 6" />
        </svg>
        <svg class="dtp-icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
          <rect x="3" y="4" width="18" height="18" rx="2" /><path d="M16 2v4M8 2v4M3 10h18" />
        </svg>
      </div>
    </div>

    <!-- 弹出面板 -->
    <Teleport to="body">
      <div v-if="open" ref="pickerRef" class="dtp-panel" :style="panelStyle">
        <div class="dtp-body">
          <!-- 左侧日历 -->
          <div class="dtp-calendar">
            <!-- 头部导航 -->
            <div class="dtp-nav">
              <button @click="prevYear" class="dtp-nav-btn" title="上一年">&laquo;</button>
              <button @click="prevMonth" class="dtp-nav-btn" title="上月">&lsaquo;</button>
              <span class="dtp-nav-title">{{ monthNames[month] }} {{ year }}</span>
              <button @click="nextMonth" class="dtp-nav-btn" title="下月">&rsaquo;</button>
              <button @click="nextYear" class="dtp-nav-btn" title="下一年">&raquo;</button>
            </div>

            <!-- 星期头 -->
            <div class="dtp-weekdays">
              <span v-for="w in weekDays" :key="w">{{ w }}</span>
            </div>

            <!-- 日期格子 -->
            <div class="dtp-days">
              <button
                v-for="(d, i) in calendarDays"
                :key="i"
                :class="[
                  'dtp-day',
                  !d.current && 'dtp-day-other',
                  d.current && 'dtp-day-current',
                  isToday(d.day, d.current) && 'dtp-day-today',
                  isSelected(d.day, d.current) && 'dtp-day-selected',
                ]"
                @click="selectDay(d.day, d.current)"
              >{{ d.day }}</button>
            </div>
          </div>

          <!-- 右侧时间 -->
          <div class="dtp-time">
            <div class="dtp-time-header">{{ pad(hour) }}:{{ pad(minute) }}:{{ pad(second) }}</div>
            <div class="dtp-time-columns">
              <div ref="hourRef" class="dtp-time-col">
                <button
                  v-for="h in hours" :key="h"
                  :class="['dtp-time-item', h === hour && 'dtp-time-active']"
                  @click="selectHour(h)"
                >{{ pad(h) }}</button>
              </div>
              <div ref="minuteRef" class="dtp-time-col">
                <button
                  v-for="m in minutes" :key="m"
                  :class="['dtp-time-item', m === minute && 'dtp-time-active']"
                  @click="selectMinute(m)"
                >{{ pad(m) }}</button>
              </div>
              <div ref="secondRef" class="dtp-time-col">
                <button
                  v-for="s in seconds" :key="s"
                  :class="['dtp-time-item', s === second && 'dtp-time-active']"
                  @click="selectSecond(s)"
                >{{ pad(s) }}</button>
              </div>
            </div>
          </div>
        </div>

        <!-- 底部操作 -->
        <div class="dtp-footer">
          <button class="dtp-footer-btn dtp-now" @click="setNow">Now</button>
          <button class="dtp-footer-btn dtp-ok" @click="confirm">OK</button>
        </div>
      </div>
    </Teleport>
  </div>
</template>

<style scoped>
.dtp-wrapper {
  position: relative;
  display: inline-block;
}

.dtp-input {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  padding: 6px 12px;
  min-width: 200px;
  height: 36px;
  background: var(--bg-card);
  border: 1px solid var(--border);
  border-radius: 8px;
  cursor: pointer;
  font-size: 13px;
  transition: border-color 0.2s;
  user-select: none;
}
.dtp-input:hover {
  border-color: var(--accent);
}

.dtp-input-icons {
  display: flex;
  align-items: center;
  gap: 4px;
  flex-shrink: 0;
}

.dtp-icon {
  width: 16px;
  height: 16px;
  color: var(--text-muted);
}

.dtp-clear {
  width: 14px;
  height: 14px;
  color: var(--text-muted);
  cursor: pointer;
  border-radius: 50%;
  transition: color 0.15s;
}
.dtp-clear:hover {
  color: var(--danger);
}

/* 弹出面板 */
.dtp-panel {
  position: fixed;
  z-index: 9999;
  margin-top: 4px;
  background: var(--bg-card);
  border: 1px solid var(--border);
  border-radius: 10px;
  box-shadow: 0 8px 32px rgba(0,0,0,0.5);
  overflow: hidden;
  animation: dtp-fade-in 0.15s ease;
}

@keyframes dtp-fade-in {
  from { opacity: 0; transform: translateY(-4px); }
  to { opacity: 1; transform: translateY(0); }
}

.dtp-body {
  display: flex;
}

/* 日历 */
.dtp-calendar {
  padding: 12px;
  width: 280px;
}

.dtp-nav {
  display: flex;
  align-items: center;
  justify-content: space-between;
  margin-bottom: 8px;
}

.dtp-nav-btn {
  background: none;
  border: none;
  color: var(--text-secondary);
  font-size: 16px;
  width: 28px;
  height: 28px;
  display: flex;
  align-items: center;
  justify-content: center;
  cursor: pointer;
  border-radius: 4px;
  transition: all 0.15s;
}
.dtp-nav-btn:hover {
  background: var(--bg-card-hover);
  color: var(--text-primary);
}

.dtp-nav-title {
  font-size: 14px;
  font-weight: 600;
  color: var(--text-primary);
}

.dtp-weekdays {
  display: grid;
  grid-template-columns: repeat(7, 1fr);
  text-align: center;
  margin-bottom: 4px;
}
.dtp-weekdays span {
  font-size: 12px;
  color: var(--text-muted);
  padding: 4px 0;
}

.dtp-days {
  display: grid;
  grid-template-columns: repeat(7, 1fr);
  gap: 2px;
}

.dtp-day {
  width: 34px;
  height: 28px;
  display: flex;
  align-items: center;
  justify-content: center;
  font-size: 13px;
  border: none;
  background: none;
  border-radius: 4px;
  cursor: pointer;
  transition: all 0.12s;
  margin: 0 auto;
}

.dtp-day-other {
  color: var(--text-muted);
  opacity: 0.4;
  cursor: default;
}

.dtp-day-current {
  color: var(--text-primary);
}
.dtp-day-current:hover {
  background: var(--bg-card-hover);
}

.dtp-day-today {
  color: var(--accent);
  font-weight: 600;
}

.dtp-day-selected {
  background: var(--accent) !important;
  color: #000 !important;
  font-weight: 600;
}

/* 时间列 */
.dtp-time {
  width: 160px;
  border-left: 1px solid var(--border);
  display: flex;
  flex-direction: column;
}

.dtp-time-header {
  text-align: center;
  padding: 10px 8px;
  font-size: 14px;
  font-weight: 600;
  color: var(--accent);
  border-bottom: 1px solid var(--border);
  font-variant-numeric: tabular-nums;
}

.dtp-time-columns {
  display: flex;
  flex: 1;
  overflow: hidden;
}

.dtp-time-col {
  flex: 1;
  overflow-y: auto;
  max-height: 224px;
  scrollbar-width: thin;
  scrollbar-color: var(--border) transparent;
}
.dtp-time-col:not(:last-child) {
  border-right: 1px solid var(--border);
}

.dtp-time-item {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 100%;
  height: 28px;
  font-size: 13px;
  color: var(--text-secondary);
  background: none;
  border: none;
  cursor: pointer;
  transition: all 0.12s;
  font-variant-numeric: tabular-nums;
}
.dtp-time-item:hover {
  background: var(--bg-card-hover);
  color: var(--text-primary);
}

.dtp-time-active {
  background: var(--accent) !important;
  color: #000 !important;
  font-weight: 600;
}

/* 底部 */
.dtp-footer {
  display: flex;
  justify-content: space-between;
  align-items: center;
  padding: 8px 12px;
  border-top: 1px solid var(--border);
}

.dtp-footer-btn {
  padding: 4px 16px;
  font-size: 13px;
  border: none;
  border-radius: 4px;
  cursor: pointer;
  transition: all 0.15s;
}

.dtp-now {
  background: none;
  color: var(--accent);
}
.dtp-now:hover {
  background: var(--bg-card-hover);
}

.dtp-ok {
  background: var(--accent);
  color: #000;
  font-weight: 600;
}
.dtp-ok:hover {
  background: var(--accent-light);
}
</style>
