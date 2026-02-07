// API 耗时统计页面 JavaScript

let apiData = [];
let currentSort = { field: 'avgTotalTime', order: 'desc' };

// 页面加载完成后初始化
$(document).ready(function() {
    refreshData();
    
    // 搜索框事件
    $('#search-input').on('input', debounce(filterAndRender, 300));
    
    // 方法筛选事件
    $('#method-filter').on('change', filterAndRender);
    
    // 排序选择事件
    $('#sort-by').on('change', function() {
        const [field, order] = $(this).val().split('-');
        currentSort = { field, order };
        filterAndRender();
    });
    
    // 表头点击排序
    $('#api-table th[data-sort]').on('click', function() {
        const field = $(this).data('sort');
        if (currentSort.field === field) {
            currentSort.order = currentSort.order === 'asc' ? 'desc' : 'asc';
        } else {
            currentSort.field = field;
            currentSort.order = 'desc';
        }
        updateSortSelect();
        filterAndRender();
    });
    
    // 自动刷新（每30秒）
    setInterval(refreshData, 30000);
});

// 刷新数据
function refreshData() {
    $.ajax({
        url: '/api/timing/list',
        method: 'GET',
        dataType: 'json',
        success: function(response) {
            if (response.success) {
                apiData = response.data || [];
                updateSummary(response.summary);
                filterAndRender();
                $('#last-update').text(new Date().toLocaleTimeString());
            } else {
                showError('获取数据失败: ' + response.message);
            }
        },
        error: function(xhr, status, error) {
            showError('请求失败: ' + error);
        }
    });
}

// 更新统计概览
function updateSummary(summary) {
    if (!summary) return;
    
    $('#total-apis').text(summary.totalApis || 0);
    $('#total-requests').text(formatNumber(summary.totalRequests || 0));
    $('#avg-total-time').text(formatTime(summary.avgTotalTime));
    $('#avg-backend-time').text(formatTime(summary.avgBackendTime));
    $('#avg-gateway-time').text(formatTime(summary.avgGatewayTime));
}

// 过滤和渲染数据
function filterAndRender() {
    let filtered = [...apiData];
    
    // 搜索过滤
    const searchTerm = $('#search-input').val().toLowerCase();
    if (searchTerm) {
        filtered = filtered.filter(item => 
            item.path.toLowerCase().includes(searchTerm) ||
            item.method.toLowerCase().includes(searchTerm)
        );
    }
    
    // 方法过滤
    const methodFilter = $('#method-filter').val();
    if (methodFilter) {
        filtered = filtered.filter(item => item.method === methodFilter);
    }
    
    // 排序
    filtered.sort((a, b) => {
        let aVal = a[currentSort.field];
        let bVal = b[currentSort.field];
        
        // 处理字符串排序
        if (typeof aVal === 'string') {
            aVal = aVal.toLowerCase();
            bVal = bVal.toLowerCase();
        }
        
        // 处理日期排序
        if (currentSort.field === 'lastRequestTime') {
            aVal = new Date(aVal).getTime();
            bVal = new Date(bVal).getTime();
        }
        
        if (currentSort.order === 'asc') {
            return aVal > bVal ? 1 : (aVal < bVal ? -1 : 0);
        } else {
            return aVal < bVal ? 1 : (aVal > bVal ? -1 : 0);
        }
    });
    
    renderTable(filtered);
    updateSortIndicators();
}

// 渲染表格
function renderTable(data) {
    const tbody = $('#api-tbody');
    tbody.empty();
    
    if (data.length === 0) {
        tbody.html('<tr><td colspan="10" class="empty-state">暂无 API 耗时数据</td></tr>');
        return;
    }
    
    data.forEach(item => {
        const row = `
            <tr>
                <td><span class="method-badge method-${item.method}">${item.method}</span></td>
                <td><code>${escapeHtml(item.path)}</code></td>
                <td>${formatNumber(item.requestCount)}</td>
                <td class="time-value ${getTimeClass(item.avgTotalTime)}">${formatTime(item.avgTotalTime)}</td>
                <td class="time-value ${getTimeClass(item.avgBackendTime)}">${formatTime(item.avgBackendTime)}</td>
                <td class="time-value">${formatTime(item.avgGatewayTime)}</td>
                <td class="time-value">${formatTime(item.minTotalTime)}</td>
                <td class="time-value ${getTimeClass(item.maxTotalTime)}">${formatTime(item.maxTotalTime)}</td>
                <td>${renderStatusCodes(item.statusCodeCounts)}</td>
                <td>${formatLastRequest(item.lastRequestTime)}</td>
            </tr>
        `;
        tbody.append(row);
    });
}

// 渲染状态码分布
function renderStatusCodes(statusCodes) {
    if (!statusCodes || Object.keys(statusCodes).length === 0) {
        return '<span style="color: var(--text-muted)">-</span>';
    }
    
    let html = '<div class="status-codes">';
    
    // 按状态码排序
    const sorted = Object.entries(statusCodes).sort((a, b) => parseInt(a[0]) - parseInt(b[0]));
    
    sorted.forEach(([code, count]) => {
        const codeNum = parseInt(code);
        let cssClass = 'status-2xx';
        if (codeNum >= 300 && codeNum < 400) cssClass = 'status-3xx';
        else if (codeNum >= 400 && codeNum < 500) cssClass = 'status-4xx';
        else if (codeNum >= 500) cssClass = 'status-5xx';
        
        html += `<span class="status-badge ${cssClass}">${code}: ${count}</span>`;
    });
    
    html += '</div>';
    return html;
}

// 获取时间样式类
function getTimeClass(ms) {
    if (ms < 100) return 'time-good';
    if (ms < 500) return '';
    if (ms < 1000) return 'time-warn';
    return 'time-bad';
}

// 格式化时间
function formatTime(ms) {
    if (ms === undefined || ms === null || ms === 0) return '-';
    if (ms < 1) return '<1ms';
    if (ms < 1000) return Math.round(ms) + 'ms';
    if (ms < 60000) return (ms / 1000).toFixed(2) + 's';
    return (ms / 60000).toFixed(2) + 'min';
}

// 格式化数字
function formatNumber(num) {
    if (num >= 1000000) return (num / 1000000).toFixed(1) + 'M';
    if (num >= 1000) return (num / 1000).toFixed(1) + 'K';
    return num.toString();
}

// 格式化最后请求时间
function formatLastRequest(dateStr) {
    if (!dateStr) return '-';
    
    const date = new Date(dateStr);
    const now = new Date();
    const diff = now - date;
    
    if (diff < 60000) return '刚刚';
    if (diff < 3600000) return Math.floor(diff / 60000) + '分钟前';
    if (diff < 86400000) return Math.floor(diff / 3600000) + '小时前';
    
    return date.toLocaleString();
}

// 更新排序选择器
function updateSortSelect() {
    const value = `${currentSort.field}-${currentSort.order}`;
    const select = $('#sort-by');
    
    if (select.find(`option[value="${value}"]`).length > 0) {
        select.val(value);
    }
}

// 更新排序指示器
function updateSortIndicators() {
    $('#api-table th').removeClass('sorted');
    $(`#api-table th[data-sort="${currentSort.field}"]`).addClass('sorted');
}

// 清除统计数据
function clearData() {
    if (!confirm('确定要清除所有 API 耗时统计数据吗？')) {
        return;
    }
    
    $.ajax({
        url: '/api/timing/clear',
        method: 'POST',
        contentType: 'application/json',
        data: JSON.stringify({}),
        success: function(response) {
            if (response.success) {
                showMessage('统计数据已清除', 'success');
                refreshData();
            } else {
                showMessage('清除失败: ' + response.message, 'error');
            }
        },
        error: function(xhr, status, error) {
            showMessage('请求失败: ' + error, 'error');
        }
    });
}

// 显示错误
function showError(message) {
    $('#api-tbody').html(`<tr><td colspan="10" class="empty-state" style="color: var(--danger)">${escapeHtml(message)}</td></tr>`);
}

// 显示消息
function showMessage(message, type) {
    alert(message);
}

// HTML 转义
function escapeHtml(text) {
    const div = document.createElement('div');
    div.textContent = text;
    return div.innerHTML;
}

// 防抖函数
function debounce(func, wait) {
    let timeout;
    return function(...args) {
        clearTimeout(timeout);
        timeout = setTimeout(() => func.apply(this, args), wait);
    };
}
