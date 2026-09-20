import { ControlsRail } from './components/ControlsRail';
import { Header } from './components/Header';
import { MessageTable } from './components/MessageTable';
import { PayloadPane } from './components/PayloadPane';

export default function App() {
    return (
        <div className="flex h-full flex-col overflow-hidden">
            <Header />
            <div className="flex min-h-0 grow">
                <ControlsRail />
                <MessageTable />
                <PayloadPane />
            </div>
        </div>
    );
}
