#pragma once
// ============================================================================
// moodycamel::ConcurrentQueue (Lock-Free MPMC Ring Queue for Project Kongor)
// Cache-line aligned sequence-stamped lock-free MPMC queue eliminating
// CRITICAL_SECTION contention between Named Pipe IPC workers and the 600+ FPS
// K2 main thread (CHost::Frame / KongorSubclassWndProc).
// ============================================================================

#include <atomic>
#include <cstddef>
#include <cstdint>
#include <type_traits>
#include <utility>
#include <vector>

namespace moodycamel {

template <typename T, size_t Capacity = 4096>
class ConcurrentQueue {
    static_assert((Capacity >= 2) && ((Capacity & (Capacity - 1)) == 0),
                  "ConcurrentQueue Capacity must be a power of two.");

private:
    struct alignas(64) Cell {
        std::atomic<size_t> sequence;
        T data;
    };

    static constexpr size_t kMask = Capacity - 1;

    Cell* _buffer;
    alignas(64) std::atomic<size_t> _enqueuePos{0};
    alignas(64) std::atomic<size_t> _dequeuePos{0};
    alignas(64) std::atomic<size_t> _approxCount{0};

public:
    ConcurrentQueue() {
        _buffer = new Cell[Capacity];
        for (size_t i = 0; i < Capacity; ++i) {
            _buffer[i].sequence.store(i, std::memory_order_relaxed);
        }
    }

    ~ConcurrentQueue() {
        delete[] _buffer;
    }

    ConcurrentQueue(const ConcurrentQueue&) = delete;
    ConcurrentQueue& operator=(const ConcurrentQueue&) = delete;

    bool enqueue(const T& item) {
        return emplace_impl(item);
    }

    bool enqueue(T&& item) {
        return emplace_impl(std::move(item));
    }

    bool try_dequeue(T& item) {
        Cell* cell = nullptr;
        size_t pos = _dequeuePos.load(std::memory_order_relaxed);
        for (;;) {
            cell = &_buffer[pos & kMask];
            size_t seq = cell->sequence.load(std::memory_order_acquire);
            intptr_t dif = static_cast<intptr_t>(seq) - static_cast<intptr_t>(pos + 1);
            if (dif == 0) {
                if (_dequeuePos.compare_exchange_weak(pos, pos + 1, std::memory_order_relaxed)) {
                    break;
                }
            } else if (dif < 0) {
                return false;
            } else {
                pos = _dequeuePos.load(std::memory_order_relaxed);
            }
        }
        item = std::move(cell->data);
        cell->sequence.store(pos + kMask + 1, std::memory_order_release);
        _approxCount.fetch_sub(1, std::memory_order_relaxed);
        return true;
    }

    template <typename OutputIt>
    size_t try_dequeue_bulk(OutputIt itemFirst, size_t max) {
        size_t count = 0;
        while (count < max) {
            T item;
            if (!try_dequeue(item)) {
                break;
            }
            *itemFirst++ = std::move(item);
            ++count;
        }
        return count;
    }

    size_t size_approx() const noexcept {
        size_t enq = _enqueuePos.load(std::memory_order_relaxed);
        size_t deq = _dequeuePos.load(std::memory_order_relaxed);
        return (enq >= deq) ? (enq - deq) : 0;
    }

private:
    template <typename U>
    bool emplace_impl(U&& item) {
        Cell* cell = nullptr;
        size_t pos = _enqueuePos.load(std::memory_order_relaxed);
        for (;;) {
            cell = &_buffer[pos & kMask];
            size_t seq = cell->sequence.load(std::memory_order_acquire);
            intptr_t dif = static_cast<intptr_t>(seq) - static_cast<intptr_t>(pos);
            if (dif == 0) {
                if (_enqueuePos.compare_exchange_weak(pos, pos + 1, std::memory_order_relaxed)) {
                    break;
                }
            } else if (dif < 0) {
                return false;
            } else {
                pos = _enqueuePos.load(std::memory_order_relaxed);
            }
        }
        cell->data = std::forward<U>(item);
        cell->sequence.store(pos + 1, std::memory_order_release);
        _approxCount.fetch_add(1, std::memory_order_relaxed);
        return true;
    }
};

} // namespace moodycamel
