WafInfos {
    Listens { proxy { Host = "127.0.0.1" Port = 19800 IsHttps = false } }
    ControlListen { Host = "127.0.0.1" Port = 19801 IsHttps = false }
}

ResponseCache {
    Enabled = false
    Directory = "temp/cache"
    MaxBodyBytes = 4096
}

cache.example.test {
    /assets/* {
        ResponseCache { Enabled = true TtlSeconds = 120 }
        reverse_proxy http://127.0.0.1:19802
    }
}

Plugins { Enabled = false }
DuckDb { Enabled = false }
ProxyServer { Enabled = false }
StreamServer { Enabled = false }
Acme { Enabled = false }
