# Configuration fragment: merge into config.ly, keeping the existing listeners/upstreams.
# Explicit Load is exhaustive: include every plugin the deployment needs.
Plugins {
    Enabled = true
    Load = ["analysis", "web-tamper", "image-process"]

    image-process {
        Enabled = false
        DefaultQuality = 85
        MaxWidth = 4096
        MaxHeight = 4096
        SupportedFormats = ["jpg", "png", "webp"]
        CacheSeconds = 3600
    }
}

# These are plugin configuration scopes, not new proxy listeners or upstream routes.
Sites {
    images {
        Hosts = ["img.example.com"]
        Plugins {
            image-process {
                Enabled = true
                DefaultQuality = 80
            }
        }
        Routes {
            thumbnails {
                Path = "/thumb/**"
                Plugins {
                    image-process {
                        DefaultQuality = 60
                        MaxWidth = 800
                        CacheSeconds = 0
                        SupportedFormats = ["png"]
                    }
                }
            }
            originals {
                Path = "/original/**"
                Plugins {
                    image-process {
                        Enabled = false
                    }
                }
            }
        }
    }
}
