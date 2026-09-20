import { useRef } from 'react';
import { clampPaneWidth, DEFAULT_PAYLOAD_WIDTH, MIN_PAYLOAD_WIDTH, savePaneWidth } from '../split';

interface Props {
    width: number;
    onResize: (width: number) => void;
}

/**
 * The divider between the message table and the payload pane.
 *
 * A real separator rather than a styled div: it takes focus, arrow keys move it, and it
 * reports its position, so resizing does not require a mouse. The visible line is a
 * hairline; the grab area around it is wider, because a 1px target is unusable.
 */
export function SplitHandle({ width, onResize }: Props) {
    const dragging = useRef(false);

    const applyAndRemember = (next: number) => {
        onResize(next);
        savePaneWidth(next);
    };

    const fromPointer = (clientX: number) =>
        clampPaneWidth(window.innerWidth - clientX, window.innerWidth);

    const onKeyDown = (event: React.KeyboardEvent) => {
        const step = event.shiftKey ? 64 : 16;

        // Left widens the payload pane, because that is the direction the divider moves.
        if (event.key === 'ArrowLeft') {
            applyAndRemember(clampPaneWidth(width + step, window.innerWidth));
        } else if (event.key === 'ArrowRight') {
            applyAndRemember(clampPaneWidth(width - step, window.innerWidth));
        } else if (event.key === 'Home' || event.key === 'Enter') {
            applyAndRemember(clampPaneWidth(DEFAULT_PAYLOAD_WIDTH, window.innerWidth));
        } else {
            return;
        }
        event.preventDefault();
    };

    return (
        <div
            role="separator"
            aria-orientation="vertical"
            aria-label="Resize the payload pane"
            aria-valuenow={width}
            aria-valuemin={MIN_PAYLOAD_WIDTH}
            aria-valuemax={Math.max(MIN_PAYLOAD_WIDTH, window.innerWidth)}
            tabIndex={0}
            onKeyDown={onKeyDown}
            onDoubleClick={() => applyAndRemember(clampPaneWidth(DEFAULT_PAYLOAD_WIDTH, window.innerWidth))}
            onPointerDown={(event) => {
                event.currentTarget.setPointerCapture(event.pointerId);
                dragging.current = true;
            }}
            onPointerMove={(event) => {
                if (dragging.current) onResize(fromPointer(event.clientX));
            }}
            onPointerUp={(event) => {
                if (!dragging.current) return;
                dragging.current = false;
                event.currentTarget.releasePointerCapture(event.pointerId);
                savePaneWidth(fromPointer(event.clientX));
            }}
            // Capture keeps the pointer on the handle once the drag starts, so the cursor can
            // outrun it without dropping the drag. touch-action stops a touch scrolling the
            // page instead of moving the divider.
            style={{ touchAction: 'none' }}
            className="group relative w-[7px] shrink-0 cursor-col-resize select-none"
            title="Drag to resize · double-click to reset"
        >
            {/* The hairline itself, centred in the wider grab area. */}
            <span className="pointer-events-none absolute inset-y-0 left-[3px] w-px bg-hairline transition-colors group-hover:bg-accent group-focus-visible:bg-accent" />
        </div>
    );
}
