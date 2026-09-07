WafInfos {
    Listens { static { Host = "127.0.0.1" Port = 19820 IsHttps = false } }
}
control_listen = "127.0.0.1:19821"

127.0.0.1:19820 {
    file_server {
        root = "tests/fixtures/static-root"
        browse = true
        precompressed = true
        follow_links = false
        show_hidden = false
        cache_control = "public, max-age=120"
    }
}

Compress { Enabled = false }
Plugins { Enabled = false }
DuckDb { Enabled = false }
ProxyServer { Enabled = false }
StreamServer { Enabled = false }
Acme { Enabled = false }
