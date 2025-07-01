package me.vbu.mirrordirectories.model;

import java.util.HashMap;
import java.util.Map;

/**
 * Represents a directory in the file system.
 * Can contain child nodes (files or other directories).
 */
public class DirectoryNode extends Node {
    private final Map<String, Node> children;

    /**
     * Creates a new DirectoryNode with the given name.
     *
     * @param name The name of the directory
     */
    public DirectoryNode(String name) {
        super(name);
        this.children = new HashMap<>();
    }

    /**
     * Checks if this node represents a directory.
     *
     * @return Always returns true as this is a directory
     */
    @Override
    public boolean isDirectory() {
        return true;
    }

    /**
     * Gets the child nodes of this directory.
     *
     * @return A map of child nodes
     */
    public Map<String, Node> getChildren() {
        return children;
    }

    /**
     * Adds a child node to this directory.
     *
     * @param child The child node to add (can be a file or directory)
     */
    public void addChild(Node child) {
        if (child != null) {
            children.put(child.getName(), child);
        }
    }

    /**
     * Checks if this directory has any children.
     *
     * @return True if this directory has children, false otherwise
     */
    public boolean hasChildren() {
        return !children.isEmpty();
    }
}
