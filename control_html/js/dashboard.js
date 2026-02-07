/**
 * LyWaf 控制面板 JavaScript
 */

$(document).ready(function() {
    initDashboard();
    
    // 自动刷新（每 60 秒）
    setInterval(function() {
        location.reload();
    }, 60000);
});

/**
 * 初始化控制面板
 */
function initDashboard() {
    updateRefreshTime();
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

// ==================== 功能状态切换 ====================

var featureNames = {
    'ip-control': 'IP 访问控制',
    'geo-control': '地理位置控制',
    'waf-args': 'WAF Args检测',
    'waf-post': 'WAF Post检测'
};

function toggleFeature(featureId) {
    var featureName = featureNames[featureId] || featureId;
    
    // 从当前元素的 class 判断当前状态，然后切换到相反状态
    var $item = $('.feature-item[data-feature="' + featureId + '"]');
    var currentEnabled = $item.hasClass('status-on');
    var newEnabled = !currentEnabled;
    
    callApi('POST', '/api/feature/' + featureId + '/toggle', { enabled: newEnabled })
        .done(function(res) {
            if (res.success) {
                var statusText = res.enabled ? '启用' : '禁用';
                showMessage('success', featureName + '已' + statusText);
                reloadAfterDelay();
            } else {
                showMessage('error', '切换失败: ' + res.message);
            }
        })
        .fail(handleApiError);
}

// ==================== IP 白名单管理 ====================

function addWhitelistIp() {
    var ip = prompt('请输入要添加到白名单的 IP 或 CIDR:');
    if (!ip) return;
    
    callApi('POST', '/api/ac/whitelist/add', { ipOrCidr: ip })
        .done(function(res) {
            if (res.success) {
                showMessage('success', '已添加到白名单: ' + ip);
                reloadAfterDelay();
            } else {
                showMessage('error', '添加失败: ' + res.message);
            }
        })
        .fail(handleApiError);
}

function removeWhitelistIp(ip) {
    if (!confirm('确定要从白名单移除 ' + ip + ' 吗？')) return;
    
    callApi('POST', '/api/ac/whitelist/remove', { ipOrCidr: ip })
        .done(function(res) {
            if (res.success) {
                showMessage('success', '已从白名单移除: ' + ip);
                reloadAfterDelay();
            } else {
                showMessage('error', '移除失败: ' + res.message);
            }
        })
        .fail(handleApiError);
}

// ==================== IP 黑名单管理 ====================

function addBlacklistIp() {
    var ip = prompt('请输入要添加到黑名单的 IP 或 CIDR:');
    if (!ip) return;
    
    callApi('POST', '/api/ac/blacklist/add', { ipOrCidr: ip })
        .done(function(res) {
            if (res.success) {
                showMessage('success', '已添加到黑名单: ' + ip);
                reloadAfterDelay();
            } else {
                showMessage('error', '添加失败: ' + res.message);
            }
        })
        .fail(handleApiError);
}

function removeBlacklistIp(ip) {
    if (!confirm('确定要从黑名单移除 ' + ip + ' 吗？')) return;
    
    callApi('POST', '/api/ac/blacklist/remove', { ipOrCidr: ip })
        .done(function(res) {
            if (res.success) {
                showMessage('success', '已从黑名单移除: ' + ip);
                reloadAfterDelay();
            } else {
                showMessage('error', '移除失败: ' + res.message);
            }
        })
        .fail(handleApiError);
}

// ==================== 地理位置访问控制 ====================

function addAllowCountry() {
    var country = prompt('请输入允许访问的国家名称 (如: 中国):');
    if (!country) return;
    
    callApi('POST', '/api/geo/allow-countries/add', { country: country })
        .done(function(res) {
            if (res.success) {
                showMessage('success', '已添加允许访问国家: ' + country);
                reloadAfterDelay();
            } else {
                showMessage('error', '添加失败: ' + res.message);
            }
        })
        .fail(handleApiError);
}

function removeAllowCountry(country) {
    if (!confirm('确定要移除允许访问的国家 "' + country + '" 吗？')) return;
    
    callApi('POST', '/api/geo/allow-countries/remove', { country: country })
        .done(function(res) {
            if (res.success) {
                showMessage('success', '已移除: ' + country);
                reloadAfterDelay();
            } else {
                showMessage('error', '移除失败: ' + res.message);
            }
        })
        .fail(handleApiError);
}

function addAllowRegion() {
    var region = prompt('请输入允许访问的省份名称 (如: 广东省):');
    if (!region) return;
    
    callApi('POST', '/api/geo/allow-regions/add', { region: region })
        .done(function(res) {
            if (res.success) {
                showMessage('success', '已添加允许访问省份: ' + region);
                reloadAfterDelay();
            } else {
                showMessage('error', '添加失败: ' + res.message);
            }
        })
        .fail(handleApiError);
}

function removeAllowRegion(region) {
    if (!confirm('确定要移除允许访问的省份 "' + region + '" 吗？')) return;
    
    callApi('POST', '/api/geo/allow-regions/remove', { region: region })
        .done(function(res) {
            if (res.success) {
                showMessage('success', '已移除: ' + region);
                reloadAfterDelay();
            } else {
                showMessage('error', '移除失败: ' + res.message);
            }
        })
        .fail(handleApiError);
}

function addDenyCountry() {
    var country = prompt('请输入禁止访问的国家名称:');
    if (!country) return;
    
    callApi('POST', '/api/geo/deny-countries/add', { country: country })
        .done(function(res) {
            if (res.success) {
                showMessage('success', '已添加禁止访问国家: ' + country);
                reloadAfterDelay();
            } else {
                showMessage('error', '添加失败: ' + res.message);
            }
        })
        .fail(handleApiError);
}

function removeDenyCountry(country) {
    if (!confirm('确定要移除禁止访问的国家 "' + country + '" 吗？')) return;
    
    callApi('POST', '/api/geo/deny-countries/remove', { country: country })
        .done(function(res) {
            if (res.success) {
                showMessage('success', '已移除: ' + country);
                reloadAfterDelay();
            } else {
                showMessage('error', '移除失败: ' + res.message);
            }
        })
        .fail(handleApiError);
}

function addDenyRegion() {
    var region = prompt('请输入禁止访问的省份名称:');
    if (!region) return;
    
    callApi('POST', '/api/geo/deny-regions/add', { region: region })
        .done(function(res) {
            if (res.success) {
                showMessage('success', '已添加禁止访问省份: ' + region);
                reloadAfterDelay();
            } else {
                showMessage('error', '添加失败: ' + res.message);
            }
        })
        .fail(handleApiError);
}

function removeDenyRegion(region) {
    if (!confirm('确定要移除禁止访问的省份 "' + region + '" 吗？')) return;
    
    callApi('POST', '/api/geo/deny-regions/remove', { region: region })
        .done(function(res) {
            if (res.success) {
                showMessage('success', '已移除: ' + region);
                reloadAfterDelay();
            } else {
                showMessage('error', '移除失败: ' + res.message);
            }
        })
        .fail(handleApiError);
}

// ==================== WAF 规则管理 ====================

function addArgsRule() {
    var regex = prompt('请输入 Args 检测正则表达式:');
    if (!regex) return;
    
    callApi('POST', '/api/waf/args/add', { regex: regex })
        .done(function(res) {
            if (res.success) {
                showMessage('success', '已添加 Args 规则');
                reloadAfterDelay();
            } else {
                showMessage('error', '添加失败: ' + res.message);
            }
        })
        .fail(handleApiError);
}

function removeArgsRule(regex) {
    if (!confirm('确定要删除此 Args 规则吗？')) return;
    
    callApi('POST', '/api/waf/args/remove', { regex: regex })
        .done(function(res) {
            if (res.success) {
                showMessage('success', '已删除 Args 规则');
                reloadAfterDelay();
            } else {
                showMessage('error', '删除失败: ' + res.message);
            }
        })
        .fail(handleApiError);
}

function addPostRule() {
    var regex = prompt('请输入 POST 检测正则表达式:');
    if (!regex) return;
    
    callApi('POST', '/api/waf/post/add', { regex: regex })
        .done(function(res) {
            if (res.success) {
                showMessage('success', '已添加 POST 规则');
                reloadAfterDelay();
            } else {
                showMessage('error', '添加失败: ' + res.message);
            }
        })
        .fail(handleApiError);
}

function removePostRule(regex) {
    if (!confirm('确定要删除此 POST 规则吗？')) return;
    
    callApi('POST', '/api/waf/post/remove', { regex: regex })
        .done(function(res) {
            if (res.success) {
                showMessage('success', '已删除 POST 规则');
                reloadAfterDelay();
            } else {
                showMessage('error', '删除失败: ' + res.message);
            }
        })
        .fail(handleApiError);
}

// ==================== CC 规则管理 ====================

function addCcRule() {
    var path = prompt('请输入限制路径 (留空表示全局):', '/');
    if (path === null) return;
    
    var limitNum = prompt('请输入请求次数限制:', '100');
    if (!limitNum) return;
    
    var period = prompt('请输入时间窗口 (秒):', '60');
    if (!period) return;
    
    var fbSeconds = prompt('请输入封禁时长 (秒):', '300');
    if (!fbSeconds) return;
    
    callApi('POST', '/api/cc/rules/add', {
        path: path || '/',
        limitNum: parseInt(limitNum),
        period: parseInt(period),
        fbSeconds: parseInt(fbSeconds)
    })
        .done(function(res) {
            if (res.success) {
                showMessage('success', '已添加 CC 规则');
                reloadAfterDelay();
            } else {
                showMessage('error', '添加失败: ' + res.message);
            }
        })
        .fail(handleApiError);
}

function removeCcRule(path) {
    if (!confirm('确定要删除路径 "' + path + '" 的 CC 规则吗？')) return;
    
    callApi('POST', '/api/cc/rules/remove', { path: path })
        .done(function(res) {
            if (res.success) {
                showMessage('success', '已删除 CC 规则');
                reloadAfterDelay();
            } else {
                showMessage('error', '删除失败: ' + res.message);
            }
        })
        .fail(handleApiError);
}

// ==================== 封禁 IP 管理 ====================

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
                reloadAfterDelay();
            } else {
                showMessage('error', '封禁失败: ' + res.message);
            }
        })
        .fail(handleApiError);
}

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
                reloadAfterDelay();
            } else {
                showMessage('error', '解除失败: ' + res.message);
            }
        })
        .fail(handleApiError);
}

function clearBlockedIps() {
    if (!confirm('确定要清空所有封禁的 IP 吗？')) return;
    
    $.post('/api/blocked-ips/clear')
        .done(function(res) {
            if (res.success) {
                showMessage('success', '已清空所有封禁 IP');
                reloadAfterDelay();
            } else {
                showMessage('error', '操作失败: ' + res.message);
            }
        })
        .fail(handleApiError);
}

// ==================== A/B 测试管理 ====================

function toggleABTest(testId, currentEnabled) {
    var newEnabled = !currentEnabled;
    var action = newEnabled ? '启用' : '禁用';
    
    if (!confirm('确定要' + action + ' A/B 测试: ' + testId + ' 吗？')) return;
    
    callApi('POST', '/api/abtest/configs/' + testId + '/toggle', { enabled: newEnabled })
        .done(function(res) {
            if (res.success) {
                showMessage('success', '已' + action + ' A/B 测试: ' + testId);
                reloadAfterDelay();
            } else {
                showMessage('error', '操作失败: ' + res.message);
            }
        })
        .fail(handleApiError);
}

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
        .fail(handleApiError);
}

// ==================== 工具函数 ====================

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
 * 处理 API 错误
 */
function handleApiError(xhr) {
    var msg = '请求失败';
    if (xhr.responseJSON && xhr.responseJSON.message) {
        msg = xhr.responseJSON.message;
    } else if (xhr.status === 0) {
        msg = '网络连接失败';
    } else {
        msg = '请求失败 (' + xhr.status + ')';
    }
    showMessage('error', msg);
}

/**
 * 延迟后刷新页面
 */
function reloadAfterDelay(delay) {
    delay = delay || 800;
    setTimeout(function() {
        location.reload();
    }, delay);
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
