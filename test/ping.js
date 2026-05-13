import http from 'k6/http';
import { check } from 'k6';
import { Counter } from 'k6/metrics';

const errorCount = new Counter('error_count');

export const options = {
    summaryTrendStats: ['p(99)'],
    systemTags: ['status', 'method'],
    dns: {
        ttl: '5m',
        select: 'roundRobin',
    },
    scenarios: {
        default: {
            executor: 'ramping-arrival-rate',
            startRate: 1,
            timeUnit: '1s',
            preAllocatedVUs: 250,
            maxVUs: 250,
            gracefulStop: '10s',
            stages: [
                { duration: '120s', target: 900 },
            ],
        },
    },
};

export default function () {
    const target = 'http://localhost:9999';
    const res = http.get(`${target}/ping`, { timeout: '2001ms' });

    const ok = check(res, {
        'status 200':       (r) => r.status === 200,
        'connection reused': (r) => r.timings.connecting === 0,
    });

    if (!ok) errorCount.add(1);
}
