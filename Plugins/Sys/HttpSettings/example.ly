# Configuration fragment only: keep existing listeners/upstreams.
# Explicit Load is exhaustive; include ALL plugins required by your deployment.
Plugins {
    Load = ["http-settings"]
    http-settings {
        SecurityHeaders = true
        RequestIdHeader = "X-Request-ID"
        RemoveResponseHeaders = ["X-Powered-By"]
    }
}
Sites {
    example {
        Hosts = ["example.com"]
        Routes {
            api {
                Path = "/api/**"
                Plugins {
                    http-settings {
                        ResponseHeaders {
                            Cache-Control = "no-store"
                        }
                    }
                }
            }
        }
    }
}
