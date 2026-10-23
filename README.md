# Crosshair Client Launcher (CCL)

一个从零编写的 Minecraft: Java 版第三方启动器，Windows 桌面应用，使用 .NET 10 + WPF。

本项目不使用、不包含任何其他启动器的源代码，架构与实现均为独立编写。

## 功能

- **游戏安装**：解析官方版本清单，下载客户端、库文件与资源对象，SHA-1 校验，断点续传，官方源失败自动降级到 BMCLAPI 镜像
- **启动游戏**：Java 自动探测、classpath 组装、参数模板求值、日志接管
- **实例管理**：扫描本地版本、删除实例、标识未完成的安装
- **下载任务队列**：顺序执行、单项取消、全部取消、失败不阻断后续任务
- **账号**：离线账号；微软账号走官方 OAuth 2.0 设备码流程（需自备通过审核的 Azure client id）
- **界面**：暗色玻璃风格，中文 / English 双语，可跟随系统

## 构建

需要 .NET 10 SDK。

```powershell
dotnet build CCL.slnx -c Debug
dotnet run --project "src/CCL.App/CCL.App.csproj"
```

运行测试：

```powershell
dotnet test CCL.slnx
```

## 项目结构

```
src/CCL.Core/          平台无关的核心库
  Net/                 下载、重试、镜像降级、JSON 接口客户端
  Hashing/             哈希计算与内容寻址存储
  Minecraft/           版本清单、库解析、安装、实例扫描
  Launching/           Java 探测、参数组装、进程启动
  Accounts/            微软设备码认证、账号持久化（DPAPI 加密）
  Operations/          任务队列
src/CCL.App/           WPF 界面（MVVM）
tests/CCL.Core.Tests/  单元测试
```

## 登录说明

微软账号登录使用官方 OAuth 2.0 设备码流程，scope 为 `XboxLive.signin offline_access`，并在换取 Minecraft 令牌前通过 `/entitlements/mcstore` 校验账号确实拥有 Minecraft: Java 版。

Mojang 目前对访问 Java 版游戏服务 API 的第三方应用实行人工允许列表，新应用需先通过 [AppID 审核](https://aka.ms/AppRegInfo) 才能换取令牌。在审核通过前，可在设置中填写自己的 Azure client id，或使用离线账号。

访问令牌与刷新令牌仅保存在本机，使用 Windows DPAPI（当前用户作用域）加密，不上传任何服务器；启动器不含遥测。

## 许可

待定。
