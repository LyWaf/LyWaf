# LyWaf Frontend

基于 Vue 3 + Vite + TypeScript 的 LyWaf 控制面板前端项目。

## 技术栈

- **Vue 3** - 渐进式 JavaScript 框架
- **Vite** - 下一代前端构建工具
- **TypeScript** - JavaScript 的超集
- **Vue Router** - Vue.js 官方路由
- **Pinia** - Vue 状态管理
- **Tailwind CSS** - 原子化 CSS 框架
- **Chart.js** - 图表库
- **Axios** - HTTP 客户端

## 项目结构

```
Frontend/
├── public/                 # 静态资源
├── src/
│   ├── api/               # API 服务层
│   ├── components/        # 组件
│   │   ├── common/        # 通用组件
│   │   ├── dashboard/     # Dashboard 相关组件
│   │   └── Layout/        # 布局组件
│   ├── composables/       # 组合式函数
│   ├── router/            # 路由配置
│   ├── stores/            # 状态管理
│   ├── styles/            # 样式文件
│   ├── types/             # TypeScript 类型定义
│   ├── views/             # 页面视图
│   ├── App.vue            # 根组件
│   └── main.ts            # 入口文件
├── index.html
├── package.json
├── tailwind.config.js
├── tsconfig.json
└── vite.config.ts
```

## 功能模块

### 统计报表 (Dashboard)
- 流量分析统计
- 系统状态监控
- 功能开关控制
- IP 黑白名单管理
- 地理访问控制
- WAF 规则管理
- CC 防护规则
- 封禁 IP 管理

### 安全态势 (Security)
- 攻击拦截趋势图
- CC 攻击统计
- 黑名单拦截统计
- 地理拦截统计
- 爬虫检测统计
- 频率限制统计
- Top 攻击源 IP 列表

### API 耗时 (ApiTiming)
- API 请求统计汇总
- 后端稳定性分析
- 请求耗时详情表格
- 状态码分布统计
- 筛选和排序功能

## 开发

### 安装依赖

```bash
npm install
# 或
pnpm install
```

### 启动开发服务器

```bash
npm run dev
# 或
pnpm dev
```

开发服务器默认运行在 http://localhost:3000

### 构建生产版本

```bash
npm run build
# 或
pnpm build
```

构建输出在 `dist` 目录。

## 配置

### API 代理

开发环境下，API 请求会代理到 `http://127.0.0.1:7030`。可在 `vite.config.ts` 中修改：

```typescript
server: {
  proxy: {
    '/api': {
      target: 'http://127.0.0.1:7030',
      changeOrigin: true,
    },
  },
}
```

### 环境变量

创建 `.env.local` 文件配置环境变量：

```env
VITE_API_BASE_URL=http://your-api-server
```

## 与后端集成

构建完成后，将 `dist` 目录内容部署到 LyWaf 的静态文件服务目录，或配置反向代理。

## 许可证

MIT
