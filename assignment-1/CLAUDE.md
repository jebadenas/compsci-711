# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

COMPSCI 711 Assignment 1 — implements **total order multicast** across a distributed middleware system. Five middleware instances communicate through a central network simulator that imposes configurable delivery delays to test ordering guarantees.

## Build & Run

**Build all programs (Windows, from project root):**
```
run.bat
```
This compiles all 6 C# programs with `csc` and launches them in separate windows.

**Build individual program:**
```
csc /out:network\network.exe network\network.cs
csc /out:middleware8082\middleware8082.exe middleware8082\middleware8082.cs
```

**On macOS/Linux:** Use `mcs` (Mono C# compiler) instead of `csc`:
```
mcs -out:network/network.exe network/network.cs
```

**Run order:** Start `network.exe` first, then the middleware executables.

## Architecture

```
[middleware8082] \
[middleware8083]  \
[middleware8084]  --TCP--> [network:8081] --TCP (delayed)--> all middleware
[middleware8085]  /
[middleware8086] /
```

- **Network** (`network/network.cs`, port 8081): Central multicast coordinator. Receives messages from any middleware, reads the next line from `delays.txt`, sorts delay values, then broadcasts the message to all 5 middleware with staggered delays.
- **Middleware** (`middleware808X/middleware808X.cs`, ports 8082–8086): Each is an identical Windows Forms app with a "Send Message" button and a `RichTextBox` display. Clicking Send sends a message to the network. Each middleware listens on its port for incoming multicast messages.

## Configuration

**`network/delays.txt`** — controls delivery timing. Each line corresponds to one sent message; the 5 comma-separated values are per-middleware delays in milliseconds:

```
1,8000,100,200,300
8000,100,1000,500,5000
```

- Line N is consumed when the Nth message is multicasted.
- Add more lines to support more test messages (currently only 4 lines).
- **Only `delays.txt` should be modified** — `network.cs` is provided by the course and will be replaced during grading.

## Key Implementation Details

- **Thread safety**: The network uses a `lock` on `currentLine` (integer counter) to avoid race conditions when multiple messages arrive concurrently.
- **Ordered delivery**: The network sorts delay values and sends with incremental waits (`delay[i] - delay[i-1]`) so the first-received middleware always gets the message before later ones, establishing total order.
- **Async I/O**: Both network and middleware use `async/await` with `TcpListener`/`TcpClient`.
- All middleware programs are structurally identical — only the port number and identifier string differ.

## Assignment Constraints

- Do **not** modify `network.cs` — it will be replaced at grading time.
- The assignment requires each middleware GUI to show three lists: Sent, Received, and Ready messages.
- Total order multicast must be correct for any `delays.txt` configuration the grader uses.
