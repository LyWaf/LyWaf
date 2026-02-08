// 安全态势页面 JavaScript

// 图表实例存储
const charts = {};
let currentHours = 1;

// 初始化所有图表
function initCharts() {
    const chartConfigs = [
        { id: 'wafChart', label: 'WAF拦截', color: '#3b82f6' },
        { id: 'ccChart', label: 'CC攻击', color: '#ef4444' },
        { id: 'blacklistChart', label: '黑名单', color: '#f59e0b' },
        { id: 'geoChart', label: '地理拦截', color: '#10b981' },
        { id: 'crawlerChart', label: '爬虫检测', color: '#8b5cf6' },
        { id: 'totalChart', label: '总拦截', color: '#00d4aa' }
    ];
    
    chartConfigs.forEach(config => {
        const ctx = document.getElementById(config.id);
        if (ctx) {
            charts[config.id] = new Chart(ctx, {
                type: 'line',
                data: {
                    labels: [],
                    datasets: [{
                        label: config.label,
                        data: [],
                        borderColor: config.color,
                        backgroundColor: config.color + '20',
                        borderWidth: 2,
                        fill: true,
                        tension: 0.4,
                        pointRadius: 0,
                        pointHoverRadius: 4
                    }]
                },
                options: {
                    responsive: true,
                    maintainAspectRatio: false,
                    plugins: {
                        legend: { display: false }
                    },
                    scales: {
                        x: {
                            display: true,
                            grid: { color: '#2d374822' },
                            ticks: { 
                                color: '#64748b',
                                maxRotation: 0,
                                maxTicksLimit: 6
                            }
                        },
                        y: {
                            display: true,
                            beginAtZero: true,
                            grid: { color: '#2d374833' },
                            ticks: { color: '#64748b' }
                        }
                    },
                    interaction: {
                        intersect: false,
                        mode: 'index'
                    }
                }
            });
        }
    });
}

// 格式化数字
function formatNumber(num) {
    if (num >= 1000000) return (num / 1000000).toFixed(1) + 'M';
    if (num >= 1000) return (num / 1000).toFixed(1) + 'k';
    return num.toString();
}

// 格式化时间
function formatTime(dateStr) {
    const date = new Date(dateStr);
    return date.toLocaleTimeString('zh-CN', { hour: '2-digit', minute: '2-digit' });
}

// 格式化日期时间
function formatDateTime(dateStr) {
    const date = new Date(dateStr);
    return date.toLocaleString('zh-CN');
}

// 更新统计卡片
function updateStats(snapshot) {
    $('#wafIntercept').text(formatNumber(snapshot.wafInterceptCount || 0));
    $('#blacklistBlock').text(formatNumber(snapshot.blacklistBlockCount || 0));
    $('#ccAttack').text(formatNumber(snapshot.ccAttackCount || 0));
    $('#geoBlock').text(formatNumber(snapshot.geoBlockCount || 0));
    $('#crawlerDetect').text(formatNumber(snapshot.crawlerDetectCount || 0));
    $('#rateLimit').text(formatNumber(snapshot.rateLimitCount || 0));
    $('#uniqueAttackIps').text(snapshot.uniqueAttackIps || 0);
    
    if (snapshot.startTime) {
        $('#startTime').text(formatDateTime(snapshot.startTime));
    }
}

// 更新图表数据
function updateChart(chartId, timeSlots, dataKey) {
    const chart = charts[chartId];
    if (!chart || !timeSlots) return;
    
    const labels = timeSlots.map(s => formatTime(s.time));
    const data = timeSlots.map(s => s[dataKey] || 0);
    
    chart.data.labels = labels;
    chart.data.datasets[0].data = data;
    chart.update('none');
}

// 更新IP列表
function updateIpList(listId, ipList) {
    const $list = $(`#${listId}`);
    $list.empty();
    
    if (!ipList || ipList.length === 0) {
        $list.append('<li class="ip-item"><span class="ip-addr">暂无数据</span></li>');
        return;
    }
    
    ipList.slice(0, 5).forEach(item => {
        const ip = item.ip || item.item1;
        const count = item.count || item.item2;
        $list.append(`
            <li class="ip-item">
                <span class="ip-addr">${escapeHtml(ip)}</span>
                <span class="ip-count">${formatNumber(count)}</span>
            </li>
        `);
    });
}

// HTML转义
function escapeHtml(text) {
    if (!text) return '';
    const div = document.createElement('div');
    div.textContent = text;
    return div.innerHTML;
}

// 获取并刷新数据
async function refreshData() {
    try {
        const response = await fetch(`/api/security/stats?hours=${currentHours}`);
        if (!response.ok) throw new Error('获取数据失败');
        
        const data = await response.json();
        
        // 更新统计卡片
        if (data.snapshot) {
            updateStats(data.snapshot);
        }
        
        // 更新图表
        if (data.timeSlots) {
            updateChart('wafChart', data.timeSlots, 'wafIntercept');
            updateChart('ccChart', data.timeSlots, 'ccAttack');
            updateChart('blacklistChart', data.timeSlots, 'blacklistBlock');
            updateChart('geoChart', data.timeSlots, 'geoBlock');
            updateChart('crawlerChart', data.timeSlots, 'crawlerDetect');
            updateChart('totalChart', data.timeSlots, 'total');
        }
        
        // 更新IP列表
        if (data.topAttackSources) {
            updateIpList('totalIpList', data.topAttackSources);
        }
        if (data.topWafSources) {
            updateIpList('wafIpList', data.topWafSources);
        }
        if (data.topCcSources) {
            updateIpList('ccIpList', data.topCcSources);
        }
        if (data.topBlacklistSources) {
            updateIpList('blacklistIpList', data.topBlacklistSources);
        }
        if (data.topGeoSources) {
            updateIpList('geoIpList', data.topGeoSources);
        }
        if (data.topCrawlerSources) {
            updateIpList('crawlerIpList', data.topCrawlerSources);
        }
        
        // 更新最后刷新时间
        $('#lastUpdate').text(new Date().toLocaleTimeString('zh-CN'));
        
    } catch (error) {
        console.error('刷新数据失败:', error);
        showMessage('刷新数据失败: ' + error.message, 'error');
    }
}

// 重置统计
async function resetStats() {
    if (!confirm('确定要重置所有安全统计数据吗？此操作不可恢复。')) return;
    
    try {
        const response = await fetch('/api/security/reset', { method: 'POST' });
        const result = await response.json();
        
        if (result.success) {
            showMessage('统计数据已重置');
            refreshData();
        } else {
            showMessage(result.message || '重置失败', 'error');
        }
    } catch (error) {
        showMessage('重置失败: ' + error.message, 'error');
    }
}

// 显示消息提示
function showMessage(msg, type = 'success') {
    const color = type === 'error' ? '#ef4444' : '#10b981';
    const $msg = $(`<div style="position:fixed;top:20px;right:20px;padding:12px 24px;background:${color};color:white;border-radius:8px;z-index:9999;box-shadow:0 4px 12px rgba(0,0,0,0.3);">${msg}</div>`);
    $('body').append($msg);
    setTimeout(() => $msg.fadeOut(300, () => $msg.remove()), 3000);
}

// 时间选择器事件
$(document).on('click', '.time-btn', function() {
    const $panel = $(this).closest('.chart-panel');
    $panel.find('.time-btn').removeClass('active');
    $(this).addClass('active');
    
    // 获取选择的小时数
    const hours = parseInt($(this).data('hours'));
    currentHours = hours;
    
    // 刷新数据
    refreshData();
});

// 页面加载完成
$(document).ready(function() {
    initCharts();
    refreshData();
    
    // 自动刷新（每30秒）
    setInterval(refreshData, 30000);
});
