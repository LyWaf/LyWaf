/**
 * LyWaf 控制面板 JavaScript
 */

$(document).ready(function() {
    // 初始化
    initDashboard();
    
    // 自动刷新（每 30 秒）
    setInterval(function() {
        location.reload();
    }, 30000);
});

/**
 * 初始化控制面板
 */
function initDashboard() {
    // 更新刷新时间显示
    updateRefreshTime();
    
    // 绑定事件
    bindEvents();
}

/**
 * 更新刷新时间
 */
function updateRefreshTime() {
    var now = new Date();
    var timeStr = now.getFullYear() + '-' + 
        padZero(now.getMonth() + 1) + '-' + 
        padZero(now.getDate()) + ' ' + 
        padZero(now.getHours()) + ':' + 
        padZero(now.getMinutes()) + ':' + 
        padZero(now.getSeconds());
    $('#refreshTime').text(timeStr);
}

/**
 * 绑定事件
 */
function bindEvents() {
    // 刷新按钮
    $('#btnRefresh').on('click', function() {
        location.reload();
    });
}

/**
 * 清空所有封禁的 IP
 */
function clearBlockedIps() {
    if (!confirm('确定要清空所有封禁的 IP 吗？')) return;
    
    $.post('/api/blocked-ips/clear')
        .done(function(res) {
            if (res.success) {
                showMessage('success', '已清空所有封禁 IP');
                setTimeout(function() {
                    location.reload();
                }, 1000);
            } else {
                showMessage('error', '操作失败: ' + res.message);
            }
        })
        .fail(function() {
            showMessage('error', '请求失败，请重试');
        });
}

/**
 * 添加 IP 到黑名单
 */
function addBlacklistIp() {
    var ip = prompt('请输入要添加到黑名单的 IP 或 CIDR:');
    if (!ip) return;
    
    callApi('POST', '/api/access-control/blacklist/add', { ipOrCidr: ip })
        .done(function(res) {
            if (res.success) {
                showMessage('success', '已添加到黑名单: ' + ip);
                setTimeout(function() {
                    location.reload();
                }, 1000);
            } else {
                showMessage('error', '添加失败: ' + res.message);
            }
        })
        .fail(function() {
            showMessage('error', '请求失败，请重试');
        });
}

/**
 * 添加 IP 到白名单
 */
function addWhitelistIp() {
    var ip = prompt('请输入要添加到白名单的 IP 或 CIDR:');
    if (!ip) return;
    
    callApi('POST', '/api/access-control/whitelist/add', { ipOrCidr: ip })
        .done(function(res) {
            if (res.success) {
                showMessage('success', '已添加到白名单: ' + ip);
                setTimeout(function() {
                    location.reload();
                }, 1000);
            } else {
                showMessage('error', '添加失败: ' + res.message);
            }
        })
        .fail(function() {
            showMessage('error', '请求失败，请重试');
        });
}

/**
 * 添加禁止访问的国家
 */
function addDenyCountry() {
    var country = prompt('请输入要禁止访问的国家/地区名称:');
    if (!country) return;
    
    callApi('POST', '/api/geo/deny-countries/add', { country: country })
        .done(function(res) {
            if (res.success) {
                showMessage('success', '已添加禁止访问国家: ' + country);
                setTimeout(function() {
                    location.reload();
                }, 1000);
            } else {
                showMessage('error', '添加失败: ' + res.message);
            }
        })
        .fail(function() {
            showMessage('error', '请求失败，请重试');
        });
}

/**
 * 添加禁止访问的省份
 */
function addDenyRegion() {
    var region = prompt('请输入要禁止访问的省份名称:');
    if (!region) return;
    
    callApi('POST', '/api/geo/deny-regions/add', { region: region })
        .done(function(res) {
            if (res.success) {
                showMessage('success', '已添加禁止访问省份: ' + region);
                setTimeout(function() {
                    location.reload();
                }, 1000);
            } else {
                showMessage('error', '添加失败: ' + res.message);
            }
        })
        .fail(function() {
            showMessage('error', '请求失败，请重试');
        });
}

/**
 * 手动封禁 IP
 */
function blockIp() {
    var ip = prompt('请输入要封禁的 IP 地址:');
    if (!ip) return;
    
    var minutes = prompt('封禁时长（分钟，默认 10）:', '10');
    if (minutes === null) return;
    minutes = parseInt(minutes) || 10;
    
    var reason = prompt('封禁原因（可选）:', '手动封禁');
    
    callApi('POST', '/api/blocked-ips/add', { 
        ip: ip, 
        expireMinutes: minutes,
        reason: reason || '手动封禁'
    })
        .done(function(res) {
            if (res.success) {
                showMessage('success', '已封禁 IP: ' + ip + ' (' + minutes + '分钟)');
                setTimeout(function() {
                    location.reload();
                }, 1000);
            } else {
                showMessage('error', '封禁失败: ' + res.message);
            }
        })
        .fail(function() {
            showMessage('error', '请求失败，请重试');
        });
}

/**
 * 解除封禁 IP
 */
function unblockIp(ip) {
    if (!ip) {
        ip = prompt('请输入要解除封禁的 IP 地址:');
    }
    if (!ip) return;
    
    if (!confirm('确定要解除封禁 IP: ' + ip + ' 吗？')) return;
    
    callApi('POST', '/api/blocked-ips/remove', { ip: ip })
        .done(function(res) {
            if (res.success) {
                showMessage('success', '已解除封禁: ' + ip);
                setTimeout(function() {
                    location.reload();
                }, 1000);
            } else {
                showMessage('error', '解除失败: ' + res.message);
            }
        })
        .fail(function() {
            showMessage('error', '请求失败，请重试');
        });
}

/**
 * 切换 A/B 测试状态
 */
function toggleABTest(testId, currentEnabled) {
    var newEnabled = !currentEnabled;
    var action = newEnabled ? '启用' : '禁用';
    
    if (!confirm('确定要' + action + ' A/B 测试: ' + testId + ' 吗？')) return;
    
    callApi('POST', '/api/abtest/configs/' + testId + '/toggle', { enabled: newEnabled })
        .done(function(res) {
            if (res.success) {
                showMessage('success', '已' + action + ' A/B 测试: ' + testId);
                setTimeout(function() {
                    location.reload();
                }, 1000);
            } else {
                showMessage('error', '操作失败: ' + res.message);
            }
        })
        .fail(function() {
            showMessage('error', '请求失败，请重试');
        });
}

/**
 * 查看 A/B 测试统计
 */
function viewABTestStats(testId) {
    callApi('GET', '/api/abtest/stats/' + testId)
        .done(function(res) {
            if (res.success && res.stats) {
                var stats = res.stats;
                var msg = 'A/B 测试统计: ' + testId + '\n\n';
                msg += '总请求数: ' + stats.totalRequests + '\n';
                msg += '开始时间: ' + stats.startTime + '\n';
                msg += '最后请求: ' + stats.lastRequestTime + '\n\n';
                msg += '变体分布:\n';
                for (var variant in stats.variantHits) {
                    var hits = stats.variantHits[variant];
                    var percent = stats.totalRequests > 0 ? 
                        (hits / stats.totalRequests * 100).toFixed(2) : 0;
                    msg += '  ' + variant + ': ' + hits + ' (' + percent + '%)\n';
                }
                alert(msg);
            } else {
                showMessage('error', '获取统计失败');
            }
        })
        .fail(function() {
            showMessage('error', '请求失败，请重试');
        });
}

/**
 * API 调用封装
 */
function callApi(method, url, data) {
    return $.ajax({
        method: method,
        url: url,
        contentType: 'application/json',
        data: data ? JSON.stringify(data) : null
    });
}

/**
 * 显示消息提示
 */
function showMessage(type, message) {
    // 移除旧的消息
    $('.toast-message').remove();
    
    var bgColor = type === 'success' ? '#10b981' : 
                  type === 'error' ? '#ef4444' : 
                  type === 'warning' ? '#f59e0b' : '#3b82f6';
    
    var toast = $('<div class="toast-message"></div>')
        .css({
            position: 'fixed',
            top: '20px',
            right: '20px',
            padding: '12px 24px',
            background: bgColor,
            color: 'white',
            borderRadius: '8px',
            boxShadow: '0 4px 15px rgba(0,0,0,0.3)',
            zIndex: 10000,
            fontSize: '14px',
            fontWeight: '500',
            animation: 'slideIn 0.3s ease'
        })
        .text(message)
        .appendTo('body');
    
    // 3 秒后自动消失
    setTimeout(function() {
        toast.fadeOut(300, function() {
            $(this).remove();
        });
    }, 3000);
}

/**
 * 数字补零
 */
function padZero(num) {
    return num < 10 ? '0' + num : num;
}

/**
 * 格式化时间间隔
 */
function formatDuration(seconds) {
    if (seconds < 60) {
        return seconds + '秒';
    } else if (seconds < 3600) {
        return Math.floor(seconds / 60) + '分钟';
    } else if (seconds < 86400) {
        return Math.floor(seconds / 3600) + '小时' + Math.floor((seconds % 3600) / 60) + '分钟';
    } else {
        return Math.floor(seconds / 86400) + '天' + Math.floor((seconds % 86400) / 3600) + '小时';
    }
}

/**
 * 格式化字节数
 */
function formatBytes(bytes) {
    if (bytes < 1024) {
        return bytes + ' B';
    } else if (bytes < 1024 * 1024) {
        return (bytes / 1024).toFixed(2) + ' KB';
    } else if (bytes < 1024 * 1024 * 1024) {
        return (bytes / (1024 * 1024)).toFixed(2) + ' MB';
    } else {
        return (bytes / (1024 * 1024 * 1024)).toFixed(2) + ' GB';
    }
}
