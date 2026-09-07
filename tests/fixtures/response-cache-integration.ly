WafInfos {
    Listens { proxy { Host = "127.0.0.1" Port = 19810 IsHttps = false } }
}
control_listen = "127.0.0.1:19811"

ResponseCache {
    Enabled = false
    Directory = "cache-data"
    MaxBodyBytes = 1048576
    MaxCacheBytes = 10485760
    DefaultTtlSeconds = 60
    StaleWhileRevalidateSeconds = 5
    StaleIfErrorSeconds = 60
}

Compress {
    Enabled = true
    EnableBrotli = false
    EnableGzip = true
    MinSize = 1
}

127.0.0.1:19810 {
    ResponseCache { Enabled = true TtlSeconds = 60 }
    reverse_proxy http://127.0.0.1:19812
}

Plugins { Enabled = false }
DuckDb { Enabled = false }
ProxyServer { Enabled = false }
StreamServer { Enabled = false }
Acme { Enabled = false }
