// A minimal Go c-shared library that spawns goroutines and does
// cgo-induced scheduling work. The C host drives concurrent calls
// and sends SIGRT_2 to Go's M threads to reproduce the sigaltstack
// overflow observed in the .NET + uplink-c test suite.
//
// Build:
//   CGO_ENABLED=1 go build -buildmode=c-shared -o libgolib.so golib.go

package main

import "C"
import (
	"runtime"
	"sync"
	"time"
)

// work_iteration does enough scheduler-visible work that Go creates
// multiple Ms and goroutines are migrated between them. The key is
// that each call from C enters cgo (creating/reusing an M), and
// the goroutines spawned inside cause inter-M work stealing.
//
//export work_iteration
func work_iteration(goroutines C.int, durationMs C.int) {
	n := int(goroutines)
	dur := time.Duration(int(durationMs)) * time.Millisecond

	var wg sync.WaitGroup
	done := make(chan struct{})

	for i := 0; i < n; i++ {
		wg.Add(1)
		go func() {
			defer wg.Done()
			for {
				select {
				case <-done:
					return
				default:
					// Yield to the scheduler to encourage M migration
					// and inter-M signaling.
					runtime.Gosched()
					// Small alloc to tickle the GC.
					_ = make([]byte, 64)
				}
			}
		}()
	}

	time.Sleep(dur)
	close(done)
	wg.Wait()
}

// get_thread_count returns GOMAXPROCS so the C host knows roughly
// how many Ms to expect.
//
//export get_thread_count
func get_thread_count() C.int {
	return C.int(runtime.GOMAXPROCS(0))
}

func main() {}
