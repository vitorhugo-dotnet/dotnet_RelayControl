# Discord media v1

WebSocket subprotocol: `framerelay-media-v1`. First complete text message is `{grant}`; every subsequent message is binary. Credentials never appear in URLs. The relay validates grants with RelayControl and renews 30-second leases every 10 seconds.

Header: 40 bytes, little endian. FRM1 at 0, version 1 at 4, type at 5 (1 configuration, 2 video, 3 audio, 4 keyframe request), flags u16 at 6 (bit 0 video keyframe only), generation u32 at 8, sequence u32 at 12, payload length u32 at 16, reserved zero u32 at 20, timestamp i64 microseconds at 24, duration i64 microseconds at 32. Whole message limit 8 MiB. Reject zero generation, invalid types/flags, negative or JavaScript-unsafe times, trailing or missing bytes.

Configuration payload is UTF-8 JSON `{video:{codec,width,height,format:"annexb"},audio:{codec:"opus",sampleRate:48000,channels:2}|null}`. H.264 codec string comes from SPS profile/compatibility/level. Video contains complete Annex-B access units, parameter sets on recovery keyframes. Audio contains complete Opus packets. Control has empty payload. No base64 media.

Each generation starts with configuration and a fresh keyframe. Sequences increase strictly; gaps, backlog, reconnect and codec/size changes require recovery. Queues bound both 64 messages and 16 MiB. A viewer may request one keyframe per second. Supported browser codecs do not imply Discord support; live validation is required before rollout.
