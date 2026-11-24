//! Port of AdvisorProcessHost.cs: runs the helper as a child process.
//!
//! - No window, stdout/stderr piped (UTF-8), stdin an open pipe (EOF = the overlay is gone; decide requests are
//!   written to it), BelowNormal priority so the game keeps the CPU.
//! - stderr goes to the overlay's log, at most [`STDERR_LINES_PER_MINUTE`] lines a minute.
//! - When the helper exits by itself it starts again after 2 s, 5 s, 15 s, then every 60 s; a run that lasted
//!   [`STABLE_RUN`] resets the steps.
//! - It is killed on [`AdvisorHost::stop`] and on drop, and on Windows it sits in a kill-on-close job object, so even
//!   a crashed overlay leaves no helper behind.
//!
//! Thread-safe. Events arrive on a channel from worker threads.

use std::io::{BufRead, BufReader, Write};
use std::process::{Child, ChildStdin, Command, Stdio};
use std::sync::mpsc::Sender;
use std::sync::{Arc, Mutex, MutexGuard};
use std::time::{Duration, Instant};

use super::budget::LineBudget;
use super::command::AdvisorCommand;

pub const DEFAULT_BACKOFF: [Duration; 4] = [Duration::from_secs(2), Duration::from_secs(5), Duration::from_secs(15), Duration::from_secs(60)];
pub const STABLE_RUN: Duration = Duration::from_secs(60);
pub const STDERR_LINES_PER_MINUTE: usize = 20;

/// What the host reports (worker threads).
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum HostEvent {
    /// One stdout line of the current helper.
    Line(String),
    /// The helper exited by itself; the exit code (-1 = unknown). Not sent for [`AdvisorHost::stop`].
    Exited(i32),
}

pub type Log = Arc<dyn Fn(&str) + Send + Sync>;

struct Inner {
    args: Option<Vec<String>>,
    child: Option<Child>,
    stdin: Option<ChildStdin>,
    /// Bumped by every start/stop: callbacks of an older process are ignored.
    generation: u64,
    failures: usize,
    started: Instant,
    disposed: bool,
    start_count: u32,
    job_warned: bool,
}

struct Shared {
    command: AdvisorCommand,
    backoff: Vec<Duration>,
    log: Log,
    events: Sender<HostEvent>,
    below_normal: bool,
    budget: LineBudget,
    inner: Mutex<Inner>,
}

pub struct AdvisorHost {
    shared: Arc<Shared>,
}

impl AdvisorHost {
    /// `backoff`: restart delays (tests use short ones; the last repeats). `below_normal`: the overlay passes true;
    /// tests that wait for output pass false.
    pub fn new(command: AdvisorCommand, log: Log, events: Sender<HostEvent>, backoff: Option<Vec<Duration>>, below_normal: bool) -> Self {
        AdvisorHost {
            shared: Arc::new(Shared {
                command,
                backoff: backoff.filter(|b| !b.is_empty()).unwrap_or_else(|| DEFAULT_BACKOFF.to_vec()),
                log,
                events,
                below_normal,
                budget: LineBudget::new(STDERR_LINES_PER_MINUTE, Duration::from_secs(60)),
                inner: Mutex::new(Inner {
                    args: None,
                    child: None,
                    stdin: None,
                    generation: 0,
                    failures: 0,
                    started: Instant::now(),
                    disposed: false,
                    start_count: 0,
                    job_warned: false,
                }),
            }),
        }
    }

    /// Times a helper process was started.
    pub fn start_count(&self) -> u32 {
        self.shared.lock().start_count
    }

    pub fn process_id(&self) -> Option<u32> {
        self.shared.lock().child.as_ref().map(Child::id)
    }

    /// The arguments of the current (or pending) run; None when stopped.
    pub fn arguments(&self) -> Option<Vec<String>> {
        self.shared.lock().args.clone()
    }

    /// Keeps a helper running with `args`: starts one, or restarts it when the arguments changed. Same arguments:
    /// nothing happens, also while a restart is pending.
    pub fn run(&self, args: &[String]) {
        let s = &self.shared;
        let mut g = s.lock();
        if g.disposed || g.args.as_deref() == Some(args) {
            return;
        }
        kill_locked(&mut g);
        g.args = Some(args.to_vec());
        g.failures = 0;
        start_locked(s, &mut g);
    }

    /// Kills the helper and does not restart it (until the next [`AdvisorHost::run`]).
    pub fn stop(&self) {
        let mut g = self.shared.lock();
        if g.args.is_none() && g.child.is_none() {
            return;
        }
        kill_locked(&mut g);
        g.args = None;
        (self.shared.log)("combat advisor: stopped");
    }

    /// Writes one line to the helper's stdin (a decide request). False when no helper runs or the pipe broke.
    pub fn send(&self, line: &str) -> bool {
        let mut g = self.shared.lock();
        let Some(stdin) = g.stdin.as_mut() else { return false };
        let ok = stdin.write_all(line.as_bytes()).and_then(|_| stdin.write_all(b"\n")).and_then(|_| stdin.flush()).is_ok();
        if !ok {
            g.stdin = None;
        }
        ok
    }
}

impl Drop for AdvisorHost {
    fn drop(&mut self) {
        let mut g = self.shared.lock();
        if g.disposed {
            return;
        }
        kill_locked(&mut g);
        g.args = None;
        g.disposed = true;
    }
}

impl Shared {
    fn lock(&self) -> MutexGuard<'_, Inner> {
        self.inner.lock().unwrap_or_else(|e| e.into_inner())
    }
}

fn file_name(p: &std::path::Path) -> String {
    p.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default()
}

fn start_locked(s: &Arc<Shared>, g: &mut Inner) {
    g.generation += 1;
    let generation = g.generation;
    let mut cmd = Command::new(&s.command.file_name);
    cmd.args(s.command.command_args(g.args.as_deref().unwrap_or(&[])))
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped());
    if let Some(dir) = s.command.file_name.parent().filter(|d| !d.as_os_str().is_empty()) {
        cmd.current_dir(dir);
    }
    #[cfg(windows)]
    {
        use std::os::windows::process::CommandExt;
        const CREATE_NO_WINDOW: u32 = 0x0800_0000;
        const BELOW_NORMAL_PRIORITY_CLASS: u32 = 0x0000_4000;
        cmd.creation_flags(CREATE_NO_WINDOW | if s.below_normal { BELOW_NORMAL_PRIORITY_CLASS } else { 0 });
    }
    let mut child = match cmd.spawn() {
        Ok(c) => c,
        Err(e) => {
            (s.log)(&format!("combat advisor: could not start {}: {e}", s.command.file_name.display()));
            schedule_restart_locked(s, g, generation, None);
            return;
        }
    };
    #[cfg(windows)]
    if let Err(e) = super::job::assign(&child)
        && !g.job_warned
    {
        g.job_warned = true;
        (s.log)(&format!("combat advisor: {e}; the helper is killed on exit only"));
    }
    #[cfg(not(windows))]
    let _ = (&mut g.job_warned, s.below_normal);
    let stdout = child.stdout.take();
    let stderr = child.stderr.take();
    g.stdin = child.stdin.take();
    (s.log)(&format!("combat advisor: started pid {} ({}: {})", child.id(), s.command.source, file_name(&s.command.file_name)));
    g.child = Some(child);
    g.started = Instant::now();
    g.start_count += 1;
    if let Some(out) = stdout {
        let s2 = Arc::clone(s);
        let _ = std::thread::Builder::new().name("advisor stdout".into()).spawn(move || read_stdout(&s2, out, generation));
    }
    if let Some(err) = stderr {
        let s2 = Arc::clone(s);
        let _ = std::thread::Builder::new().name("advisor stderr".into()).spawn(move || {
            let mut r = BufReader::new(err);
            let mut buf = Vec::new();
            while matches!(r.read_until(b'\n', &mut buf), Ok(n) if n > 0) {
                let line = String::from_utf8_lossy(&buf).trim_end_matches(['\r', '\n']).to_string();
                buf.clear();
                for l in s2.budget.next(&line, Instant::now()) {
                    (s2.log)(&format!("combat advisor stderr: {l}"));
                }
            }
        });
    }
}

fn schedule_restart_locked(s: &Arc<Shared>, g: &mut Inner, generation: u64, exit_code: Option<i32>) {
    let ran = g.started.elapsed();
    if exit_code.is_some() && ran >= STABLE_RUN {
        g.failures = 0;
    }
    let delay = s.backoff[g.failures.min(s.backoff.len() - 1)];
    g.failures += 1;
    let what = match exit_code {
        Some(c) => format!("exited with code {c} after {} s", secs(ran)),
        None => "start failed".to_string(),
    };
    (s.log)(&format!("combat advisor: {what}; next try in {} s", secs(delay)));
    let s2 = Arc::clone(s);
    let _ = std::thread::Builder::new().name("advisor restart".into()).spawn(move || {
        std::thread::sleep(delay);
        let mut g = s2.lock();
        if g.disposed || g.args.is_none() || g.generation != generation {
            return; // stopped or restarted meanwhile
        }
        start_locked(&s2, &mut g);
    });
}

/// `0.#` formatting of seconds.
fn secs(d: Duration) -> String {
    let t = (d.as_secs_f64() * 10.0).round() / 10.0;
    if t.fract() == 0.0 { format!("{t:.0}") } else { format!("{t:.1}") }
}

/// Kills the current helper and cancels a pending restart (the generation bump makes the timer a no-op).
fn kill_locked(g: &mut Inner) {
    g.generation += 1;
    g.stdin = None;
    if let Some(mut c) = g.child.take() {
        let _ = c.kill();
        let _ = c.wait();
    }
}

fn read_stdout(s: &Arc<Shared>, out: std::process::ChildStdout, generation: u64) {
    let mut r = BufReader::new(out);
    let mut buf = Vec::new();
    while matches!(r.read_until(b'\n', &mut buf), Ok(n) if n > 0) {
        let line = String::from_utf8_lossy(&buf).trim_end_matches(['\r', '\n']).to_string();
        buf.clear();
        if s.lock().generation != generation {
            continue; // killed: drain without delivering
        }
        let _ = s.events.send(HostEvent::Line(line));
    }
    let child = {
        let mut g = s.lock();
        if g.disposed || g.generation != generation || g.args.is_none() {
            return;
        }
        g.stdin = None;
        g.child.take()
    };
    let code = child.map_or(-1, |mut c| c.wait().ok().and_then(|st| st.code()).unwrap_or(-1));
    {
        let mut g = s.lock();
        if g.disposed || g.generation != generation || g.args.is_none() {
            return;
        }
        schedule_restart_locked(s, &mut g, generation, Some(code));
    }
    let _ = s.events.send(HostEvent::Exited(code));
}

#[cfg(all(test, unix))]
mod tests {
    use super::*;
    use std::sync::mpsc;

    fn sh(script: &str) -> (AdvisorHost, mpsc::Receiver<HostEvent>, Arc<Mutex<Vec<String>>>) {
        let logs = Arc::new(Mutex::new(Vec::new()));
        let l2 = Arc::clone(&logs);
        let (tx, rx) = mpsc::channel();
        let cmd = AdvisorCommand { file_name: "/bin/sh".into(), leading_args: vec!["-c".into(), script.into()], source: "setting" };
        let host = AdvisorHost::new(cmd, Arc::new(move |m: &str| l2.lock().unwrap().push(m.to_string())), tx, Some(vec![Duration::from_millis(50)]), false);
        (host, rx, logs)
    }

    #[test]
    fn delivers_lines_echoes_stdin_and_restarts_after_exit() {
        // Echo one stdin line, then exit 3: the host reports the line, the exit, and starts the helper again.
        let (host, rx, logs) = sh("echo ready; read l; echo \"got $l\"; echo oops >&2; exit 3");
        host.run(&["x".into()]);
        let t = Duration::from_secs(5);
        assert_eq!(rx.recv_timeout(t), Ok(HostEvent::Line("ready".into())));
        assert!(host.send(r#"{"v":2,"type":"decide","id":1}"#));
        assert_eq!(rx.recv_timeout(t), Ok(HostEvent::Line(r#"got {"v":2,"type":"decide","id":1}"#.into())));
        assert_eq!(rx.recv_timeout(t), Ok(HostEvent::Exited(3)));
        assert_eq!(rx.recv_timeout(t), Ok(HostEvent::Line("ready".into())), "restarted after the backoff");
        assert_eq!(host.start_count(), 2);
        host.run(&["x".into()]);
        assert_eq!(host.start_count(), 2, "same arguments: no restart");
        host.stop();
        assert!(host.arguments().is_none() && host.process_id().is_none());
        assert!(!host.send("x"));
        let logs = logs.lock().unwrap();
        assert!(logs.iter().any(|l| l == "combat advisor stderr: oops"), "{logs:?}");
        assert!(logs.iter().any(|l| l.starts_with("combat advisor: exited with code 3 after ") && l.ends_with("; next try in 0.1 s")), "{logs:?}");
    }

    #[test]
    fn a_missing_exe_is_retried() {
        let logs = Arc::new(Mutex::new(Vec::new()));
        let l2 = Arc::clone(&logs);
        let (tx, _rx) = mpsc::channel();
        let cmd = AdvisorCommand { file_name: "/nonexistent/helper".into(), leading_args: vec![], source: "default" };
        let host = AdvisorHost::new(cmd, Arc::new(move |m: &str| l2.lock().unwrap().push(m.to_string())), tx, Some(vec![Duration::from_millis(20)]), false);
        host.run(&[]);
        std::thread::sleep(Duration::from_millis(150));
        drop(host);
        let n = logs.lock().unwrap().iter().filter(|l| l.contains("start failed")).count();
        assert!(n >= 2, "{n}");
    }
}
