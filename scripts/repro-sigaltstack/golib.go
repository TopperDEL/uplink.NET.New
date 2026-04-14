// A Go c-shared library that generates enough scheduler pressure and
// call depth to overflow Go's 16 KB sigaltstack when SIGRT_2 arrives.
//
// The key is deep, recursive, scheduler-visible work that keeps goroutines
// migrating between Ms and makes the signal handler's stack frame large
// when it interrupts the scheduler mid-operation.
//
// Build:
//   CGO_ENABLED=1 go build -buildmode=c-shared -o libgolib.so golib.go

package main

import "C"
import (
	"runtime"
	"sync"
	"sync/atomic"
	"time"
)

// deepWork does recursive work with enough stack depth that if the
// goroutine is interrupted by a signal mid-call, the signal handler
// inherits a deep stack context. Each level does scheduler-visible
// operations (channel send, Gosched, allocation).
func deepWork(depth int, ch chan<- int, counter *atomic.Int64) {
	if depth <= 0 {
		ch <- 1
		return
	}
	// Allocate to trigger GC write barriers and potential GC pauses.
	buf := make([]byte, 256)
	buf[0] = byte(depth)
	_ = buf

	// Yield to scheduler — this is where M migration and work-stealing
	// happen, which triggers inter-M SIGRT_2 signaling.
	runtime.Gosched()

	counter.Add(1)

	// Recurse deeper. Each level adds ~100-200 bytes to the goroutine
	// stack. The signal handler runs on the sigaltstack, not the goroutine
	// stack, but a deep scheduler call chain at the point of signal
	// delivery means the handler has more scheduler bookkeeping to do.
	deepWork(depth-1, ch, counter)
}

// fanOut creates a tree of goroutines: each goroutine spawns `fan`
// children at each level, up to `levels` deep. This creates bursty
// goroutine creation/destruction that stresses the scheduler.
func fanOut(levels, fan int, wg *sync.WaitGroup, counter *atomic.Int64) {
	if levels <= 0 {
		counter.Add(1)
		runtime.Gosched()
		return
	}
	for i := 0; i < fan; i++ {
		wg.Add(1)
		go func() {
			defer wg.Done()
			fanOut(levels-1, fan, wg, counter)
		}()
	}
}

//export work_iteration
func work_iteration(goroutines C.int, durationMs C.int) {
	n := int(goroutines)
	dur := time.Duration(int(durationMs)) * time.Millisecond

	var wg sync.WaitGroup
	var counter atomic.Int64
	done := make(chan struct{})

	// Phase 1: long-running goroutines with deep recursive work.
	for i := 0; i < n; i++ {
		wg.Add(1)
		go func() {
			defer wg.Done()
			ch := make(chan int, 1)
			for {
				select {
				case <-done:
					return
				default:
					deepWork(32, ch, &counter)
					<-ch
				}
			}
		}()
	}

	// Phase 2: bursty fan-out goroutines alongside the long-runners.
	// This creates rapid goroutine churn that amplifies scheduler
	// inter-M signaling.
	wg.Add(1)
	go func() {
		defer wg.Done()
		for {
			select {
			case <-done:
				return
			default:
				var fanWg sync.WaitGroup
				fanOut(4, 5, &fanWg, &counter) // 5^4 = 625 goroutines per burst
				fanWg.Wait()
				runtime.Gosched()
			}
		}
	}()

	time.Sleep(dur)
	close(done)
	wg.Wait()
}

//export get_thread_count
func get_thread_count() C.int {
	return C.int(runtime.GOMAXPROCS(0))
}

func main() {}
