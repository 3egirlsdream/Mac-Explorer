#include <mach/mach.h>
#include <stdint.h>

extern "C" __attribute__((visibility("default")))
int64_t me_memory_footprint() {
    task_vm_info_data_t info = {};
    mach_msg_type_number_t count = TASK_VM_INFO_COUNT;
    if (task_info(mach_task_self(), TASK_VM_INFO, (task_info_t)&info, &count) != KERN_SUCCESS
        || count < TASK_VM_INFO_REV1_COUNT) return -1;
    return (int64_t)info.phys_footprint;
}
