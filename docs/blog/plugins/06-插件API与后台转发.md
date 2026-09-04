# 统一插件 API 与后台转发

插件控制台 API 实现 `IPluginApiHandler`，由核心统一入口转发：

```text
/api/plugins/{pluginId}/proxy/{pluginPath}
```

插件内部只处理相对路径：

```csharp
public sealed class MyPlugin : LyWafPluginBase, IPluginApiHandler
{
    public async Task<IResult> HandleApiAsync(string path, HttpContext context)
    {
        path = path.Trim('/');
        if (HttpMethods.IsGet(context.Request.Method) && path == "status")
            return Results.Json(new { success = true, enabled = true });

        if (HttpMethods.IsPost(context.Request.Method) && path == "items")
        {
            var input = await context.Request.ReadFromJsonAsync<CreateItemRequest>();
            return input is null ? Results.BadRequest() : Results.Ok();
        }
        return Results.NotFound();
    }
}
```

不要新增 `/api/my-plugin/*` 或修改核心 `ControlApi`。统一入口只监听控制端端口，并在分派前检查插件存在、状态可用以及是否实现 API Handler，从而保持认证、启停和路由边界一致。

前端统一调用：

```ts
import { pluginProxyApi } from '@/api'

const api = pluginProxyApi('my-plugin')
export const myPluginApi = {
  status: () => api.get<{ success: boolean; enabled: boolean }>('status'),
  create: (name: string) => api.post('items', { name }),
}
```

Handler 应验证方法、内容类型、模型和权限，限制请求体大小，并返回一致的 `{ success, message, ... }`。长任务返回任务 ID，由页面轮询状态，不要无限延长 HTTP 请求。

## 路由设计

插件路径内部按资源建模，不要把动作全部写成动词端点：

```text
GET    status
GET    items
POST   items
PUT    items/{id}
DELETE items/{id}
POST   items/{id}/check
```

`pluginPath` 可能为空，且来自 URL。分段后应逐段解码和验证，资源 ID 用 `Uri.UnescapeDataString` 或框架绑定处理，不要通过字符串拼接构造文件路径或 SQL。

## 统一响应与错误

| 情况 | 建议状态码 |
|---|---|
| 输入格式错误 | `400` |
| 未认证或无权限 | `401` / `403` |
| 资源不存在 | `404` |
| 状态冲突 | `409` |
| 插件未启用 | 统一入口返回 `503` |
| 内部异常 | `500`，日志保留详细信息 |

不要把异常堆栈返回浏览器。可预期失败返回短消息和稳定错误码，前端再映射为用户提示。

## 超时和取消

前端检查类操作应显式设置超时；后端把 `context.RequestAborted` 传给服务和网络调用。任务超过 HTTP 生命周期时改为后台任务，不要在客户端断开后继续无限占用资源。

## 验证示例

```powershell
curl.exe http://localhost:5000/api/plugins/my-plugin/proxy/status
curl.exe -X POST http://localhost:5000/api/plugins/my-plugin/proxy/items `
  -H "Content-Type: application/json" `
  -d '{"name":"demo"}'
```

将端口替换为控制端口。随后禁用插件再次请求，应得到 `503`；使用错误插件 ID 应得到 `404`。浏览器网络面板中不应出现 `/api/my-plugin/*`。
