# DNS SpeedBench

A small Windows desktop utility for comparing DNS resolver response times.

Run `publish\DnsSpeedBench.exe`. Enter one DNS server IP address per line, choose a hostname and the number of queries, then select **Run benchmark**. It measures direct UDP DNS queries to port 53 and reports average, fastest, slowest, successful-query count, and failures.

The default list includes Kovu's Pi-hole (`192.168.0.98`) alongside Cloudflare, Google, and Quad9.
