# LoupixDeck.Plugin.Speedtest

Runs a network speed test from a touch button and shows live progress, then download, upload and
ping on the button.

## Providers

Pick the provider per button in the command menu (**Speedtest → Speedtest (…)**):

| Provider | Measures | Notes |
| --- | --- | --- |
| Cloudflare | down, up, ping | `speed.cloudflare.com` endpoints, nearest Cloudflare data center |
| M-Lab | down, up, ping | NDT7 protocol (Measurement Lab), nearest M-Lab server, fixed 10 s per direction |
| LibreSpeed | down, up, ping | Nearest public server, or your own backend (plugin settings) |
| iperf3 | down, up | LAN test against your own `iperf3 -s`; needs the `iperf3` executable installed |

Press the button to start a test; press it again while it runs to cancel. Only one test runs at a time.

## Settings

- Duration per direction and number of parallel connections (Cloudflare, LibreSpeed).
- LibreSpeed backend URL (empty = nearest public server).
- iperf3 server, port, duration, parallel streams and executable path. **Check iperf3** tests the setup.

## Build

```bash
dotnet build -c Release
```

## Try it locally

```bash
loupix install          # packs the plugin and copies it into the LoupixDeck plugins folder
```

Restart LoupixDeck afterwards. `loupix install --debug` targets the plugins folder of a Debug build.

## Release

`loupix pack` writes the Plugin Store files to `dist/`. For a store release, bump `version` in
`plugin.json`, then publish a GitHub release tagged `v<version>`; the workflow in
`.github/workflows/release.yml` builds and attaches the files.
