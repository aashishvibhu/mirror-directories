package me.vbu.mirrordirectories.model;

/**
 * Abstract base class representing an item in the file system hierarchy.
 * This can be extended to represent specific types like files or directories.
 */
public abstract class Node {
    private final String name;

    /**
     * Creates a new Node with the given name.
     *
     * @param name The name of the node
     */
    public Node(String name) {
        this.name = name;
    }

    /**
     * Gets the name of the node.
     *
     * @return The name of the node
     */
    public String getName() {
        return name;
    }

    /**
     * Checks if this node represents a directory.
     *
     * @return True if this node is a directory, false if it's a file
     */
    public abstract boolean isDirectory();

    @Override
    public String toString() {
        return name + (isDirectory() ? " [DIR]" : " [FILE]");
    }
}
