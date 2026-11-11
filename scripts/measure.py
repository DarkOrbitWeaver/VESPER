#!/usr/bin/env python3
"""Run a command; print wall seconds and peak RSS (MiB) of the process tree's largest member. Dev tool only."""
import resource, subprocess, sys, time
t = time.monotonic(); rc = subprocess.call(sys.argv[1:], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
print(f"rc={rc} wall_s={time.monotonic()-t:.2f} peak_rss_mib={resource.getrusage(resource.RUSAGE_CHILDREN).ru_maxrss/1024:.1f}")
