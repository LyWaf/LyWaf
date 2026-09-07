param(
    [Parameter(Mandatory = $true)][int]$Port,
    [Parameter(Mandatory = $true)][string]$Body
)

$listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $Port)
$listener.Start()
$crlf = [Environment]::NewLine
try {
    while ($true) {
        $client = $listener.AcceptTcpClient()
        try {
            $stream = $client.GetStream()
            $buffer = New-Object byte[] 4096
            do {
                $read = $stream.Read($buffer, 0, $buffer.Length)
                $request = [System.Text.Encoding]::ASCII.GetString($buffer, 0, $read)
            } while ($read -gt 0 -and -not $request.Contains($crlf + $crlf))
            $payload = [System.Text.Encoding]::UTF8.GetBytes($Body)
            $headerText = "HTTP/1.1 200 OK" + $crlf + "Content-Type: text/plain" + $crlf +
                "Content-Length: " + $payload.Length + $crlf + "Connection: close" + $crlf + $crlf
            $headers = [System.Text.Encoding]::ASCII.GetBytes($headerText)
            $stream.Write($headers, 0, $headers.Length)
            $stream.Write($payload, 0, $payload.Length)
        }
        finally { $client.Dispose() }
    }
}
finally { $listener.Stop() }
