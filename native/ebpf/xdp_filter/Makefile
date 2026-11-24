# SPDX-License-Identifier: GPL-2.0-only OR BSD-2-Clause
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)
# Project Kongor eBPF/XDP Filter Makefile
CLANG ?= clang
LLC ?= llc
CC ?= gcc

BPF_CFLAGS ?= -O2 -g -Wall -target bpf -D__TARGET_ARCH_x86

all: kongor_xdp_filter.bpf.o

kongor_xdp_filter.bpf.o: kongor_xdp_filter.bpf.c kongor_xdp_filter.h
	$(CLANG) $(BPF_CFLAGS) -c $< -o $@

clean:
	rm -f kongor_xdp_filter.bpf.o

.PHONY: all clean
