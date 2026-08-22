# Security and privacy

## Trust boundary

A selected STRM file is an administrator-controlled media source that Emby can already access. The plugin accepts one stable HTTP(S) URL from that file. Same-host redirect hops remain inside that source authority; every cross-host target requires an administrator rule.

Rules support exact DNS names, exact IP addresses, CIDR ranges and label-bounded subdomain patterns. `*.example.com` matches subdomains such as `edge.example.com` and keeps the bare domain and lookalike suffixes outside the rule.

Each redirect hop is parsed before connection. Accepted targets use HTTP or HTTPS, contain no userinfo, fragment or control characters, and prevent HTTPS-to-HTTP downgrade. The redirect hop limit is configurable from 1 to 8.

HLS URI lines and URI attributes pass through the same cross-host policy before a child ticket is issued.

## STRM input

- rooted local `.strm` regular file
- maximum 16 KiB
- strict UTF-8 with optional BOM
- one non-empty HTTP(S) record
- stable length and modification time across the read
- ancestor reparse-point checks before and after reading

The local principal able to replace media-library files remains part of the Emby library trust boundary.

## Capability tickets

- generated with a cryptographic random-number generator
- 256 bits of entropy
- Base64URL representation
- memory-only payload
- item, media source, runtime generation and optional user binding
- 10-minute preview window
- playback lifetime derived from runtime plus reconnect grace
- 24-hour absolute maximum
- independent playback and HLS capacities

External players may use a ticket without forwarding Emby authentication. When an authenticated Emby identity is present, the gateway verifies it against the ticket's user binding. Tickets are bearer capabilities and can appear in Emby or reverse-proxy access logs; redact `/StrmBridge/Playback/` paths and keep access-log retention bounded.

The standard static-video adapter derives its ticket binding from the authorization context of the current Emby request. It requires `Static=true`, an exact media-source ID, a selected library, a local `.strm` owner and an eligible static source whose URL equals the current STRM value. Other video requests retain Emby's native execution.

The FFmpeg adapter applies the same item, library, local-file, exact media-source ID and current-source checks to a single in-memory transcode job. It uses only the loopback API origin reported by Emby and never writes the generated route to the library item, STRM file, configuration or media-information snapshot.

## Upstream headers

Request forwarding uses a fixed allowlist:

- `Range`
- `If-Range`
- `If-None-Match`
- `If-Modified-Since`
- `Accept`
- `Accept-Language`
- `Cache-Control`

The upstream request uses `Accept-Encoding: identity`. Emby authorization, cookies and API keys remain outside upstream requests. The transport has no shared cookie jar.

Response forwarding uses status codes and a fixed metadata allowlist including content type, content length, content range, range support, validators and content disposition. Gateway responses force private, non-cacheable handling instead of forwarding upstream cache policy.

## Logging

Plugin logs contain fixed event names and these bounded dynamic fields:

- exception type
- ABI version
- target count
- transport event
- item ID first eight hexadecimal characters
- aggregate count

Plugin logs exclude paths, hosts, URLs, query values, signatures, `Location`, User-Agent values, tickets, headers, media titles, library names and user names. Debug logging keeps the same field policy.

Detected-host persistence contains normalized exact hostnames only. It excludes schemes, ports, paths, queries, fragments and credentials. Administrator activities and notifications contain aggregate counts.

Redirect leases are keyed by a SHA-256 digest of the capability ticket, HTTP method and normalized request context. Range values are excluded so nearby byte requests can share one lease. Effective addresses exist only in bounded process memory, receive redirect-policy validation on every use, expire after 30 seconds, and are cleared with runtime-sensitive state. Generation checks prevent late in-flight resolutions from repopulating cleared leases.

## Failure behavior

PlaybackInfo processing retains the native response after ABI, mapping, ticket or rewrite failure. Standard static-video requests and per-job FFmpeg state retain native execution until every STRM scope and source check has passed; a transcode mutation failure restores the original state and revokes its ticket. Gateway capacity returns 503 with a bounded retry hint, upstream header or body-idle timeout returns 504, and trust or source validation failure returns a uniform unavailable response. A fresh redirect target or leased target returning 401, 403, 404 or 410 is released and resolved once from the original source. The same total timeout bounds both attempts, direct sources are not retried, and the second result is returned without another retry. Configuration invalidation and shutdown cancel active control operations and clear memory-only tickets and redirect leases.
