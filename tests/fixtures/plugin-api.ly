WafInfos {
    Listens {
        proxy { Host = "127.0.0.1" Port = 19443 IsHttps = false }
    }
    ControlListen { Host = "127.0.0.1" Port = 19444 IsHttps = false }
}

Plugins {
    Enabled = false
    ApiProxy {
        MaxRequestBodyBytes = 1048576
        RequestsPerMinute = 120
        MaxConcurrentRequests = 16
    }
}

DuckDb { Enabled = false }
ProxyServer { Enabled = false }
StreamServer { Enabled = false }
Acme { Enabled = false }

ServiceDiscovery {
    Enabled = false
    DebounceMilliseconds = 500
    AllowEmpty = false
    Providers {
        file { Type = "File" ClusterId = "cluster" Path = "discovery.json" RefreshSeconds = 10 }
        dns { Type = "Dns" ClusterId = "cluster" Host = "localhost" Port = 8080 RecordType = "Address" }
        srv { Type = "Dns" ClusterId = "cluster" Host = "_http._tcp.example.test" RecordType = "SRV" }
        docker { Type = "Docker" ClusterId = "cluster" Endpoint = "http://127.0.0.1:2375" Port = 8080 }
        kubernetes { Type = "Kubernetes" ClusterId = "cluster" ResourceKind = "Ingress" ResourceName = "api" Port = 0 }
    }
}

Distribution {
    Enabled = false
    Role = "Standalone"
    NodeId = "test-node"
    SharedSecret = "test-only-secret"
    StateDirectory = ".lywaf-distribution"
    MaxConfigBytes = 4194304
    RequestTimeoutSeconds = 15
    AllowedClockSkewSeconds = 60
    Nodes {
        node-a { Endpoint = "https://127.0.0.1:7030" Tags = ["canary"] }
    }
}

RuntimeState {
    Enabled = false
    NodeId = "test-node"
    StateDirectory = ".lywaf-runtime-state"
    QueueCapacity = 1000
    MaxValueBytes = 65536
    MaxJournalBytes = 1048576
    AllowedClockSkewSeconds = 60
    SharedSecret = "test-only-state-secret"
    Peers { }
}
