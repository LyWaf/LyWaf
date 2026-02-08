/**
 * 构建后自动将 dist 目录复制到 control_html
 */
import { cpSync, rmSync, existsSync, mkdirSync } from 'fs'
import { resolve, dirname } from 'path'
import { fileURLToPath } from 'url'

const __dirname = dirname(fileURLToPath(import.meta.url))

const srcDir = resolve(__dirname, '../dist')
const destDir = resolve(__dirname, '../../control_html')

console.log('📦 正在复制构建文件...')
console.log(`   源目录: ${srcDir}`)
console.log(`   目标目录: ${destDir}`)

try {
  // 如果目标目录存在，先删除
  if (existsSync(destDir)) {
    rmSync(destDir, { recursive: true, force: true })
    console.log('   ✓ 已清理旧文件')
  }

  // 创建目标目录
  mkdirSync(destDir, { recursive: true })

  // 复制文件
  cpSync(srcDir, destDir, { recursive: true })
  
  console.log('   ✓ 复制完成!')
  console.log('')
  console.log('🎉 构建成功! 前端文件已复制到 control_html 目录')
} catch (error) {
  console.error('❌ 复制失败:', error.message)
  process.exit(1)
}
