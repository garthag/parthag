# NetBoost – Wi-Fi & internet analyzer / optimizer (Windows PC)

NetBoost finds out **why** your Wi-Fi internet is slow or unstable, uses **all CPU cores and many parallel connections** to get the full speed of your line, and applies safe Windows settings that lower ping and keep the connection steady.

> Honest note: no app can make your internet faster than the plan your ISP sells you.
> NetBoost removes the bottlenecks between your PC and that limit (weak signal, crowded channel,
> slow DNS, power-saving Wi-Fi, single-connection downloads, wrong TCP settings).

## How to run (Windows)

1. Install Python from <https://www.python.org/downloads/> and tick **“Add python.exe to PATH”** during setup.
2. Copy the `netboost` folder to your PC.
3. Right-click **`NetBoost.bat` → Run as administrator** and choose from the menu:

| Option | What it does |
|---|---|
| 1. Full analysis | Wi-Fi hardware report, ping/jitter/loss, DNS benchmark, speed test (1 vs many connections), recommendations |
| 2. Analyze + apply | Same, then applies the optimizations below |
| 3. Live monitor | Pings the router and the internet every second, flags lag spikes and lost packets, shows signal |
| 4. Fast download | Downloads a file using many parallel connections (like IDM) |
| 5. Undo | Puts DNS back to automatic and Wi-Fi power saving back to default |

Command line works too: `python netboost.py analyze`, `python netboost.py monitor`, `python netboost.py download <url> -c 16`.

## What it checks

- **Receiver (your PC's Wi-Fi adapter):** adapter model, Wi-Fi standard (Wi-Fi 4/5/6/7), band (2.4/5/6 GHz), channel, signal %, RSSI, link speed both ways.
- **Sender (router/modem):** router IP, ping/jitter/loss on the Wi-Fi hop alone, nearby networks on each channel (congestion).
- **Internet/ISP:** ping/jitter/loss to Cloudflare and Google, which tells you whether the problem is your Wi-Fi or your ISP.
- **DNS:** compares Cloudflare, Google, Quad9, OpenDNS, AdGuard and your current DNS, and picks the fastest.
- **Speed:** download/upload with 1 connection, then with 2 connections per CPU core (up to 16) at the same time.

## What “apply” changes (all reversible)

| Setting | Why |
|---|---|
| RSS (Receive Side Scaling) on | Spreads network packet processing over **multiple CPU cores** |
| TCP auto-tuning = normal | Lets Windows use large TCP windows to reach full speed |
| TCP Fast Open on | Faster connection setup |
| Wi-Fi adapter power saving → max performance | Stops ping spikes and drops caused by the adapter sleeping |
| DNS → fastest server measured | Websites start loading faster |
| Flush DNS cache | Clears stale entries |

## Biggest real-world speed-ups (NetBoost tells you which apply to you)

1. Use **5 GHz** Wi-Fi instead of 2.4 GHz when near the router.
2. Move closer to the router / raise it / add a mesh node if the signal is under 60%.
3. On 2.4 GHz, set the router to the least crowded of channels **1, 6 or 11**.
4. Turn on **QoS / SQM / gaming mode** in the router if ping spikes when others use the internet.
5. Use a **LAN cable** for the lowest, most stable ping.
