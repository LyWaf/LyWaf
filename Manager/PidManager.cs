using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NLog;

namespace LyWaf.Manager;
/// <summary>
/// 高级PID文件管理器，确保单实例运行和进程健康监控
/// </summary>
public sealed class PidManager : IDisposable
{
    #region 配置常量
    private const string DEFAULT_PID_DIR_LINUX = "./";
    private const string DEFAULT_PID_DIR_WINDOWS = "./";
    #endregion

    #region 私有字段
    private readonly string _pidFilePath;
    private readonly string _pidName;
    private readonly Logger _logger = LogManager.GetCurrentClassLogger();
    private Mutex? _mutex;
    private bool _disposed = false;
    #endregion

    #region 公共属性
    /// <summary>当前进程ID</summary>
    public int ProcessId { get; }

    /// <summary>是否成功获得独占锁</summary>
    public bool IsLockAcquired { get; private set; }

    /// <summary>PID文件完整路径</summary>
    public string PidFilePath => _pidFilePath;
    #endregion

    #region 构造函数与初始化
    /// <summary>
    /// 创建PID管理器实例
    /// </summary>
    /// <param name="pidName">应用程序名称（用于生成文件名）</param>
    /// <param name="customPidDir">自定义PID目录（可选）</param>
    /// <param name="logger">日志记录器（可选）</param>
    public PidManager(string pidName, string? customPidDir = null)
    {
        _pidName = pidName ?? throw new ArgumentNullException(nameof(pidName));
        ProcessId = Environment.ProcessId;

        // 确定PID文件路径
        _pidFilePath = GetPidFilePath(pidName, customPidDir);

        // 确保目录存在
        EnsurePidDirectoryExists();

        // 尝试获取PID文件锁
        AcquirePidLock();
    }

    private static string GetPidFilePath(string pidName, string? customDir)
    {
        string directory = customDir ?? GetDefaultPidDirectory();
        string fileName = $"{pidName.ToLower().Replace(" ", "-")}";
        if(!fileName.EndsWith(".pid")) {
            fileName += ".pid";
        }
        return Path.Combine(directory, fileName);
    }

    private static string GetDefaultPidDirectory()
    {
        return Directory.GetCurrentDirectory();
    }

    private void EnsurePidDirectoryExists()
    {
        string? dir = Path.GetDirectoryName(_pidFilePath);
        if (dir != null && !Directory.Exists(dir))
        {
            try
            {
                Directory.CreateDirectory(dir);
                _logger.Info("创建PID目录: {Dir}", dir);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new InvalidOperationException(
                    $"无法创建PID目录 {dir}，权限不足。请以管理员/root运行或更改目录。", ex);
            }
        }
    }
    #endregion

    #region 核心锁机制
    /// <summary>
    /// 尝试获取PID文件独占锁（使用 Mutex 确保原子性）
    /// </summary>
    private void AcquirePidLock()
    {
        try
        {
            // 使用 Named Mutex 实现进程互斥
            var mutexName = $"Global\\LyWaf_{Path.GetFileName(_pidFilePath).Replace(".", "_").Replace(":", "_")}";
            _mutex = new Mutex(true, mutexName, out bool createdNew);
            
            if (!createdNew)
            {
                // Mutex 已存在，检查进程是否真的在运行
                if (File.Exists(_pidFilePath) && TryReadExistingPid(out int existingPid) && IsProcessAlive(existingPid))
                {
                    _mutex.Dispose();
                    _mutex = null;
                    throw new InvalidOperationException(
                        $"应用程序 '{_pidName}' 已在运行 (PID: {existingPid})");
                }
                
                // 进程已死，等待获取 Mutex
                if (!_mutex.WaitOne(1000))
                {
                    _mutex.Dispose();
                    _mutex = null;
                    throw new InvalidOperationException(
                        $"无法获取锁，'{_pidName}' 可能已在运行。");
                }
            }
            
            // 清理旧的 PID 文件（如果存在且进程已死）
            if (File.Exists(_pidFilePath))
            {
                if (TryReadExistingPid(out int existingPid) && !IsProcessAlive(existingPid))
                {
                    _logger.Warn("发现僵尸PID文件，进程 {ExistingPid} 已终止，将清理并继续", existingPid);
                    CleanupStalePidFile();
                }
            }

            // PID 文件使用普通方式写入，其他程序可以自由读取
            File.WriteAllText(_pidFilePath, ProcessId.ToString());

            IsLockAcquired = true;
            _logger.Info("PID文件锁定成功: {PidFilePath} (PID: {ProcessId})", _pidFilePath, ProcessId);

            // 注册全局退出事件
            RegisterExitHandlers();
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"获取PID文件{_pidName}锁失败: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// 读取现有PID文件内容
    /// </summary>
    private bool TryReadExistingPid(out int pid)
    {
        pid = 0;
        try
        {
            string content = File.ReadAllText(_pidFilePath).Trim();
            return int.TryParse(content, out pid);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 检查进程是否存活
    /// </summary>
    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException) // 进程不存在
        {
            return false;
        }
        catch (InvalidOperationException) // 进程已退出
        {
            return false;
        }
    }

    /// <summary>
    /// 清理僵尸PID文件
    /// </summary>
    private void CleanupStalePidFile()
    {
        try
        {
            File.Delete(_pidFilePath);
            _logger.Info("已清理僵尸PID文件: {PidFilePath}", _pidFilePath);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "清理僵尸PID文件失败: {Message}", ex.Message);
        }
    }
    #endregion


    #region 退出处理
    /// <summary>
    /// 注册各种退出事件处理器
    /// </summary>
    private void RegisterExitHandlers()
    {
        // .NET进程退出事件
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;

        // 控制台Ctrl+C/Ctrl+Break
        Console.CancelKeyPress += OnCancelKeyPress;

        // 非托管异常
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
    }

    private void OnProcessExit(object? sender, EventArgs e)
    {
        _logger.Info("进程正常退出，清理PID文件...");
        Dispose();
    }

    private void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
    {
        _logger.Info("接收到终止信号，清理PID文件...");
        e.Cancel = true; // 允许优雅退出
        Dispose();
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        _logger.Error("未处理异常，清理PID文件: {Exception}", e.ExceptionObject);
        Dispose();
    }
    #endregion

    #region 清理与Dispose模式
    /// <summary>
    /// 释放锁并删除PID文件
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;

        lock (this)
        {
            if (_disposed) return;

            try
            {
                // 删除 PID 文件
                if (File.Exists(_pidFilePath))
                {
                    File.Delete(_pidFilePath);
                }
                
                // 释放 Mutex
                DisposeMutex();

                _logger.Info("PID文件已清理: {PidFilePath}", _pidFilePath);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "清理PID文件时出错: {Message}", ex.Message);
            }
            finally
            {
                _disposed = true;
                GC.SuppressFinalize(this);
            }
        }
    }

    private void DisposeMutex()
    {
        try
        {
            if (_mutex != null)
            {
                _mutex.ReleaseMutex();
                _mutex.Dispose();
                _mutex = null;
            }
            IsLockAcquired = false;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "释放Mutex失败: {Message}", ex.Message);
        }
    }

    ~PidManager()
    {
        if (!_disposed)
        {
            _logger.Warn("PidManager未被正确释放!");
            Dispose();
        }
    }
    #endregion

    #region 辅助方法
    /// <summary>
    /// 获取当前进程信息
    /// </summary>
    public ProcessInfo GetProcessInfo()
    {
        using var process = Process.GetCurrentProcess();
        return new ProcessInfo
        {
            Id = process.Id,
            Name = process.ProcessName,
            StartTime = process.StartTime,
            MemoryUsageMB = process.WorkingSet64 / (1024 * 1024)
        };
    }

    /// <summary>
    /// 验证PID文件状态
    /// </summary>
    public bool ValidatePidFile()
    {
        if (!File.Exists(_pidFilePath)) return false;
        if (!TryReadExistingPid(out int filePid)) return false;
        return filePid == ProcessId && IsProcessAlive(filePid);
    }
    #endregion
}

#region 辅助类
/// <summary>
/// 进程信息
/// </summary>
public class ProcessInfo
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public DateTime StartTime { get; set; }
    public long MemoryUsageMB { get; set; }

    public override string ToString() =>
        $"PID: {Id}, 名称: {Name}, 启动时间: {StartTime:yyyy-MM-dd HH:mm:ss}, 内存: {MemoryUsageMB}MB";
}

#endregion