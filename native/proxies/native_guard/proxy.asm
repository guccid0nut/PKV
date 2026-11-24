.CODE

EXTERN pfnGetFileVersionInfoA:QWORD
EXTERN pfnGetFileVersionInfoByHandle:QWORD
EXTERN pfnGetFileVersionInfoExA:QWORD
EXTERN pfnGetFileVersionInfoExW:QWORD
EXTERN pfnGetFileVersionInfoSizeA:QWORD
EXTERN pfnGetFileVersionInfoSizeExA:QWORD
EXTERN pfnGetFileVersionInfoSizeExW:QWORD
EXTERN pfnGetFileVersionInfoSizeW:QWORD
EXTERN pfnGetFileVersionInfoW:QWORD
EXTERN pfnVerFindFileA:QWORD
EXTERN pfnVerFindFileW:QWORD
EXTERN pfnVerInstallFileA:QWORD
EXTERN pfnVerInstallFileW:QWORD
EXTERN pfnVerLanguageNameA:QWORD
EXTERN pfnVerLanguageNameW:QWORD
EXTERN pfnVerQueryValueA:QWORD
EXTERN pfnVerQueryValueW:QWORD

PUBLIC GetFileVersionInfoA
GetFileVersionInfoA PROC
    jmp qword ptr [pfnGetFileVersionInfoA]
GetFileVersionInfoA ENDP

PUBLIC GetFileVersionInfoByHandle
GetFileVersionInfoByHandle PROC
    jmp qword ptr [pfnGetFileVersionInfoByHandle]
GetFileVersionInfoByHandle ENDP

PUBLIC GetFileVersionInfoExA
GetFileVersionInfoExA PROC
    jmp qword ptr [pfnGetFileVersionInfoExA]
GetFileVersionInfoExA ENDP

PUBLIC GetFileVersionInfoExW
GetFileVersionInfoExW PROC
    jmp qword ptr [pfnGetFileVersionInfoExW]
GetFileVersionInfoExW ENDP

PUBLIC GetFileVersionInfoSizeA
GetFileVersionInfoSizeA PROC
    jmp qword ptr [pfnGetFileVersionInfoSizeA]
GetFileVersionInfoSizeA ENDP

PUBLIC GetFileVersionInfoSizeExA
GetFileVersionInfoSizeExA PROC
    jmp qword ptr [pfnGetFileVersionInfoSizeExA]
GetFileVersionInfoSizeExA ENDP

PUBLIC GetFileVersionInfoSizeExW
GetFileVersionInfoSizeExW PROC
    jmp qword ptr [pfnGetFileVersionInfoSizeExW]
GetFileVersionInfoSizeExW ENDP

PUBLIC GetFileVersionInfoSizeW
GetFileVersionInfoSizeW PROC
    jmp qword ptr [pfnGetFileVersionInfoSizeW]
GetFileVersionInfoSizeW ENDP

PUBLIC GetFileVersionInfoW
GetFileVersionInfoW PROC
    jmp qword ptr [pfnGetFileVersionInfoW]
GetFileVersionInfoW ENDP

PUBLIC VerFindFileA
VerFindFileA PROC
    jmp qword ptr [pfnVerFindFileA]
VerFindFileA ENDP

PUBLIC VerFindFileW
VerFindFileW PROC
    jmp qword ptr [pfnVerFindFileW]
VerFindFileW ENDP

PUBLIC VerInstallFileA
VerInstallFileA PROC
    jmp qword ptr [pfnVerInstallFileA]
VerInstallFileA ENDP

PUBLIC VerInstallFileW
VerInstallFileW PROC
    jmp qword ptr [pfnVerInstallFileW]
VerInstallFileW ENDP

PUBLIC VerLanguageNameA
VerLanguageNameA PROC
    jmp qword ptr [pfnVerLanguageNameA]
VerLanguageNameA ENDP

PUBLIC VerLanguageNameW
VerLanguageNameW PROC
    jmp qword ptr [pfnVerLanguageNameW]
VerLanguageNameW ENDP

PUBLIC VerQueryValueA
VerQueryValueA PROC
    jmp qword ptr [pfnVerQueryValueA]
VerQueryValueA ENDP

PUBLIC VerQueryValueW
VerQueryValueW PROC
    jmp qword ptr [pfnVerQueryValueW]
VerQueryValueW ENDP

; ============================================================================
; VID_D3D9 TEXTURE SLOT RECLAIMER & INVALID-CACHE-BYPASS HOOKS
; ============================================================================

EXTERN g_pfnVidResumeCacheHit:QWORD
EXTERN g_pfnVidResumeCacheMiss:QWORD
EXTERN Kongor_ReclaimTextureSlot:PROC
EXTERN g_pfnVidResumeAllocSuccess:QWORD
EXTERN g_pfnVidResumeAllocFail:QWORD

PUBLIC HookStub_CheckTextureSlotValid
HookStub_CheckTextureSlotValid PROC
    mov eax, dword ptr [rbx + 40h]
    cmp eax, 0FFFFFFFFh
    je CacheMiss
    mov dword ptr [r15 + 98h], eax
    mov eax, dword ptr [rbx + 44h]
    mov dword ptr [r15 + 9Ch], eax
    mov eax, 1
    jmp qword ptr [g_pfnVidResumeCacheHit]
CacheMiss:
    jmp qword ptr [g_pfnVidResumeCacheMiss]
HookStub_CheckTextureSlotValid ENDP

PUBLIC HookStub_NoFreeTextureSlots
HookStub_NoFreeTextureSlots PROC
    push rax
    push rbx
    push rcx
    push rdx
    push rsi
    push r8
    push r9
    push r10
    push r11
    push r12
    push r13
    push r14
    push r15
    
    sub rsp, 28h
    call Kongor_ReclaimTextureSlot
    add rsp, 28h
    
    mov r11d, eax
    
    pop r15
    pop r14
    pop r13
    pop r12
    pop r11
    pop r10
    pop r9
    pop r8
    pop rsi
    pop rdx
    pop rcx
    pop rbx
    pop rax
    
    cmp r11d, 0FFFFFFFFh
    je AllocFailed
    
    mov edi, r11d
    mov dword ptr [rsp + 80h], edi
    jmp qword ptr [g_pfnVidResumeAllocSuccess]

AllocFailed:
    jmp qword ptr [g_pfnVidResumeAllocFail]
HookStub_NoFreeTextureSlots ENDP

END
