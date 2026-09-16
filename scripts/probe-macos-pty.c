/* Diagnostic only: establish native C TIOCEXCL behavior on the hosted PTY driver.
 * This does not exercise USB serial hardware or replace the managed PTY tests. */
#include <errno.h>
#include <fcntl.h>
#include <stdio.h>
#include <sys/ioctl.h>
#include <unistd.h>
#include <util.h>

int main(void)
{
    int master = -1;
    int slave = -1;
    char path[1024];
    if (openpty(&master, &slave, path, NULL, NULL) != 0) {
        perror("openpty");
        return 1;
    }
    if (ioctl(slave, TIOCEXCL) != 0) {
        perror("TIOCEXCL");
        close(slave);
        close(master);
        return 1;
    }
    errno = 0;
    int competitor = open(path, O_RDWR | O_NOCTTY | O_NONBLOCK | O_CLOEXEC);
    int saved_errno = errno;
    printf("Native C PTY probe: uid=%u TIOCEXCL=0 competing_open=%s errno=%d\n",
           (unsigned)geteuid(), competitor < 0 ? "denied" : "allowed", saved_errno);
    if (competitor >= 0) {
        close(competitor);
    }
    close(slave);
    close(master);
    return competitor < 0 && saved_errno != EBUSY ? 1 : 0;
}
