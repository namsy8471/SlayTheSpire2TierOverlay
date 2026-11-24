//! A process-wide job object with JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE (port of KillOnCloseJob): when the overlay dies
//! in any way, Windows closes the job handle and kills the helper.

use std::os::windows::io::AsRawHandle;
use std::process::Child;
use std::sync::OnceLock;

use windows::Win32::Foundation::HANDLE;
use windows::Win32::System::JobObjects::{
    AssignProcessToJobObject, CreateJobObjectW, JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE, JOBOBJECT_EXTENDED_LIMIT_INFORMATION,
    JobObjectExtendedLimitInformation, SetInformationJobObject,
};

struct Job(HANDLE);
// The handle is only used with thread-safe kernel calls.
unsafe impl Send for Job {}
unsafe impl Sync for Job {}

static JOB: OnceLock<Result<Job, String>> = OnceLock::new();

fn create() -> Result<Job, String> {
    unsafe {
        let h = CreateJobObjectW(None, None).map_err(|e| format!("no job object ({e})"))?;
        let mut info = JOBOBJECT_EXTENDED_LIMIT_INFORMATION::default();
        info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        SetInformationJobObject(h, JobObjectExtendedLimitInformation, &info as *const _ as *const _, std::mem::size_of::<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>() as u32)
            .map_err(|e| format!("job object limit failed ({e})"))?;
        Ok(Job(h))
    }
}

pub fn assign(child: &Child) -> Result<(), String> {
    let job = JOB.get_or_init(create).as_ref().map_err(Clone::clone)?;
    unsafe { AssignProcessToJobObject(job.0, HANDLE(child.as_raw_handle())) }.map_err(|e| format!("could not add the helper to the job object ({e})"))
}
