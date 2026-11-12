CXX ?= g++
CXXFLAGS ?= -std=c++17 -O2 -Wall -Wextra -Werror
LIBS =
UNAME_S := $(shell uname -s)
ifeq ($(UNAME_S),Darwin)
LIBNAME = libionos_channel.dylib
LIBFLAGS = -dynamiclib -fPIC
else
LIBNAME = libionos_channel.so
LIBFLAGS = -shared -fPIC
endif

.PHONY: all test clean
all: $(LIBNAME) ionos-wav
$(LIBNAME): IonosChannel.cpp IonosChannel.h IonosGaussianTaps.h
	$(CXX) $(CXXFLAGS) $(LIBFLAGS) -o $@ IonosChannel.cpp $(LIBS)
ionos-wav: IonosChannel.cpp IonosChannel.h IonosGaussianTaps.h wav_cli.cpp
	$(CXX) $(CXXFLAGS) -o $@ IonosChannel.cpp wav_cli.cpp $(LIBS)
ionos-tests: IonosChannel.cpp IonosChannel.h IonosGaussianTaps.h tests.cpp
	$(CXX) $(CXXFLAGS) -o $@ IonosChannel.cpp tests.cpp $(LIBS)
test: ionos-tests
	./ionos-tests
clean:
	rm -f $(LIBNAME) ionos-wav ionos-tests
