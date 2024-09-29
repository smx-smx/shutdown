cmake_minimum_required(VERSION 3.30)
project(poc C ASM)
string(APPEND CMAKE_ASM_FLAGS " -x assembler-with-cpp")

add_library(shellcode shellcode.S)
add_custom_command(
	OUTPUT ${CMAKE_BINARY_DIR}/shellcode.bin
	COMMAND objcopy -O binary $<TARGET_OBJECTS:shellcode> ${CMAKE_BINARY_DIR}/shellcode.bin
	DEPENDS shellcode
)
add_custom_target(gen_shellcode ALL DEPENDS ${CMAKE_BINARY_DIR}/shellcode.bin)
