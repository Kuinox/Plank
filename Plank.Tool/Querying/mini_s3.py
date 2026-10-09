"""Local range server with latency and a shared, linearly ramping byte budget."""
import math
import socket
import threading
import time
from dataclasses import dataclass
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import unquote, urlparse


@dataclass(frozen=True)
class Settings:
    latency_ms: float = 100
    initial_throughput_mib_s: float = 1.25
    ramp_up_ms: float = 700
    max_throughput_mib_s: float = 53

    def __post_init__(self):
        values = (self.latency_ms, self.initial_throughput_mib_s,
                  self.ramp_up_ms, self.max_throughput_mib_s)
        if (not all(math.isfinite(v) for v in values) or self.latency_ms < 0 or
                self.ramp_up_ms < 0 or self.initial_throughput_mib_s <= 0 or
                self.max_throughput_mib_s < self.initial_throughput_mib_s):
            raise ValueError('Latency and ramp time must be finite and nonnegative; '
                             'throughputs must be finite, positive, and initial <= maximum')

    def duration(self, count, age):
        """Time to send count bytes starting age seconds into the ramp."""
        initial = self.initial_throughput_mib_s * 1048576
        maximum = self.max_throughput_mib_s * 1048576
        ramp = self.ramp_up_ms / 1000
        if ramp == 0 or initial == maximum or age >= ramp:
            return count / maximum
        slope = (maximum - initial) / ramp
        rate = initial + slope * max(0, age)
        left = ramp - max(0, age)
        available = rate * left + slope * left * left / 2
        if count >= available:
            return left + (count - available) / maximum
        # Stable inverse of the integral of a linearly increasing rate.
        return 2 * count / (math.sqrt(rate * rate + 2 * slope * count) + rate)


class ByteBudget:
    def __init__(self, settings, stop):
        self.settings, self.stop = settings, stop
        self.lock = threading.Lock()
        self.started = None
        self.available = 0

    def reserve(self, count, now):
        with self.lock:
            if self.started is None:
                self.started = now
            begin = max(now, self.available)
            end = begin + self.settings.duration(count, begin - self.started)
            self.available = end
            return end

    def send(self, count):
        due = self.reserve(count, time.perf_counter())
        if self.stop.wait(max(0, due - time.perf_counter())):
            raise ConnectionAbortedError('Simulator stopped')


class MiniS3:
    """One independent simulation per query; payload ramp shared across reads.

    The ramp begins with the first payload and survives gaps/connection reuse.
    HEAD consumes latency but no payload budget. Idle time earns no burst credit.
    """
    def __init__(self, files, settings=Settings()):
        self.files = {name: Path(path) for name, path in files.items()}
        self.settings = settings
        self.stop = threading.Event()
        self.budget = ByteBudget(settings, self.stop)
        self.events = []
        self.condition = threading.Condition()
        self.active = 0
        self.origin = time.perf_counter_ns()
        owner = self

        class Handler(BaseHTTPRequestHandler):
            protocol_version = 'HTTP/1.1'

            def setup(self):
                super().setup()
                self.connection.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)

            def log_message(self, *args):
                pass

            def do_HEAD(self):
                owner.forward(self, 'HEAD')

            def do_GET(self):
                owner.forward(self, 'GET')

        self.server = ThreadingHTTPServer(('127.0.0.1', 0), Handler)
        self.server.daemon_threads = True
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()

    @property
    def endpoint(self):
        return f'http://127.0.0.1:{self.server.server_port}'

    def reset_clock(self):
        self.origin = time.perf_counter_ns()
        return self.origin

    def elapsed(self):
        return (time.perf_counter_ns() - self.origin) / 1e6

    def forward(self, handler, method):
        event = dict(method=method, time=self.elapsed(), start=None, end=None,
                     bytes=0, first_byte_ms=None, status=None)
        with self.condition:
            self.active += 1
        try:
            name = unquote(urlparse(handler.path).path.rsplit('/', 1)[-1])
            path = self.files.get(name)
            if path is None:
                handler.send_error(404)
                event['status'] = 404
                return
            size = path.stat().st_size
            start, end = 0, size - 1
            range_ = handler.headers.get('Range')
            if range_:
                try:
                    if not range_.startswith('bytes=') or ',' in range_:
                        raise ValueError()
                    a, b = range_[6:].split('-')
                    if not a:
                        if int(b) <= 0:
                            raise ValueError()
                        start = max(0, size - int(b))
                    else:
                        start = int(a)
                        end = min(size - 1, int(b)) if b else size - 1
                    if start < 0 or start >= size or end < start:
                        raise ValueError()
                except (ValueError, TypeError):
                    handler.send_response(416)
                    handler.send_header('Content-Range', f'bytes */{size}')
                    handler.send_header('Content-Length', '0')
                    handler.end_headers()
                    event['status'] = 416
                    return
            event['status'] = 206 if range_ else 200
            if method == 'GET':
                event.update(start=start, end=end)
            if self.stop.wait(self.settings.latency_ms / 1000):
                raise ConnectionAbortedError('Simulator stopped')
            handler.send_response(event['status'])
            handler.send_header('Content-Length', str(end - start + 1))
            handler.send_header('Content-Type', 'application/octet-stream')
            handler.send_header('Accept-Ranges', 'bytes')
            handler.send_header('ETag', f'"{name}-{size}"')
            handler.send_header('Last-Modified', 'Thu, 01 Jan 1970 00:00:00 GMT')
            if range_:
                handler.send_header('Content-Range', f'bytes {start}-{end}/{size}')
            handler.end_headers()
            if method == 'GET':
                with path.open('rb') as source:
                    source.seek(start)
                    left = end - start + 1
                    # Deliver the first byte independently of chunk pacing.
                    while left:
                        chunk = source.read(min(1 if event['bytes'] == 0 else 32768, left))
                        if not chunk:
                            raise EOFError('File ended before advertised size')
                        self.budget.send(len(chunk))
                        handler.wfile.write(chunk)
                        event['bytes'] += len(chunk)
                        left -= len(chunk)
                        if event['first_byte_ms'] is None:
                            event['first_byte_ms'] = self.elapsed()
            handler.wfile.flush()
        except (BrokenPipeError, ConnectionResetError, ConnectionAbortedError):
            event['aborted'] = True
            handler.close_connection = True
        except Exception as error:
            event['error'] = str(error)
            handler.close_connection = True
        finally:
            event['duration'] = self.elapsed() - event['time']
            with self.condition:
                self.events.append(event)
                self.active -= 1
                self.condition.notify_all()

    def wait_idle(self, timeout=5):
        with self.condition:
            return self.condition.wait_for(lambda: self.active == 0, timeout)

    def close(self):
        self.stop.set()
        self.server.shutdown()
        self.server.server_close()
        self.thread.join()
        self.wait_idle()

    def __enter__(self):
        return self

    def __exit__(self, *args):
        self.close()
