import { useEffect, useState } from 'react';
import { ControlsRail } from './components/ControlsRail';
import { Header } from './components/Header';
import { MessageTable } from './components/MessageTable';
import { PayloadPane } from './components/PayloadPane';
import { SplitHandle } from './components/SplitHandle';
import { clampPaneWidth, loadPaneWidth } from './split';

export default function App() {
    const [payloadWidth, setPayloadWidth] = useState(() =>
        clampPaneWidth(loadPaneWidth(), window.innerWidth),
    );

    // A window that shrinks below what the current split allows re-clamps rather than
    // squeezing the table to nothing.
    useEffect(() => {
        const onResize = () => setPayloadWidth((w) => clampPaneWidth(w, window.innerWidth));
        window.addEventListener('resize', onResize);
        return () => window.removeEventListener('resize', onResize);
    }, []);

    return (
        <div className="flex h-full flex-col overflow-hidden">
            <Header />
            <div className="flex min-h-0 grow">
                <ControlsRail />
                <MessageTable />
                <SplitHandle width={payloadWidth} onResize={setPayloadWidth} />
                <PayloadPane width={payloadWidth} />
            </div>
        </div>
    );
}
